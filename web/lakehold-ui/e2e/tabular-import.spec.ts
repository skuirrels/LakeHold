import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { issueOwnerToken, signIn, testTenant } from './credential';

/**
 * Loading a file is the first thing a person does with an empty lakehouse, and it had no browser
 * coverage at all — no spec mentioned CSV, XLSX, or the import dialog. The component tests cover
 * parsing and form state; what they cannot cover is whether the upload reaches DuckLake and leaves
 * a table that SQL can read back, which is the only thing the person actually wanted.
 */
const tablePrefix = 'imported_';

const csv = ['region,units,revenue', 'north,12,1500.50', 'south,7,880.25', 'east,19,2410.00'].join(
  '\n',
);

function uniqueTable(): string {
  return `${tablePrefix}${Date.now().toString(36)}${Math.floor(Math.random() * 1e4).toString(36)}`;
}

/** Drops anything this suite created, including from a run that failed before its own cleanup. */
async function dropImportedTables(request: APIRequestContext): Promise<void> {
  const token = await issueOwnerToken(request, `e2e-import-cleanup-${Date.now()}`);
  const headers = { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };
  const query = `/api/tenants/${testTenant}/catalogs/analytics/query`;

  const listed = await request.post(query, {
    headers,
    data: { sql: `SELECT table_name FROM duckdb_tables() WHERE table_name LIKE '${tablePrefix}%'` },
  });
  if (!listed.ok()) {
    return;
  }

  const { rows } = (await listed.json()) as { rows: unknown[][] };
  for (const [name] of rows) {
    await request.post(query, { headers, data: { sql: `DROP TABLE IF EXISTS main."${name}"` } });
  }
}

async function openWorkbench(page: Page): Promise<void> {
  const tenants = page.waitForResponse(
    (response) => response.url().endsWith('/api/tenants') && response.request().method() === 'GET',
  );
  await page.goto('/workbench');
  expect((await tenants).ok()).toBe(true);
  await expect(page.getByLabel('SQL editor')).toBeVisible();
}

async function openFileImport(page: Page): Promise<void> {
  await page
    .locator('#workbench-navigation')
    .getByRole('button', { name: 'Add data', exact: true })
    .click();
  await expect(page.getByRole('heading', { name: 'Add data' })).toBeVisible();
  await page.getByRole('button', { name: 'Import data' }).click();
}

async function returnToEditor(page: Page): Promise<void> {
  await page
    .locator('#workbench-navigation')
    .getByRole('button', { name: 'Workbench', exact: true })
    .click();
  await expect(page.getByLabel('SQL editor')).toBeVisible();
}

test.describe('loading a file into the lakehouse', () => {
  // An upload crosses the browser, the API, and a DuckLake write, and the first one in a session
  // also warms DuckDB's CSV path. The default 30s is not enough headroom for that on a cold stack.
  test.setTimeout(90_000);

  test.beforeEach(async ({ page, request }) => {
    await signIn(page, request);
    await openWorkbench(page);
  });

  test.afterEach(async ({ request }) => {
    await dropImportedTables(request);
  });

  test('imports a CSV as a table and queries it back', async ({ page }) => {
    const table = uniqueTable();

    await openFileImport(page);
    // The component element wraps the trigger button as well as the modal, so it is never hidden;
    // the modal itself is what opens and closes.
    const dialog = page.getByRole('dialog', { name: 'Import a file as a table' });
    await expect(dialog).toBeVisible();

    await dialog.locator('input[type="file"]').setInputFiles({
      name: 'regions.csv',
      mimeType: 'text/csv',
      buffer: Buffer.from(csv),
    });

    // The dialog acknowledges the file before anything is written. It does not preview columns at
    // this point — asserting a column preview here would be asserting UI that does not exist.
    await expect(dialog).toContainText('regions.csv');

    await dialog.getByLabel('New table').fill(table);
    await dialog.getByRole('button', { name: 'Create table' }).click();

    // The dialog reports its own outcome and waits to be dismissed; it does not close itself.
    // These are the specific claims it makes, so they are what gets asserted: the row count it
    // actually wrote, and the types it inferred from the file rather than a generic success tick.
    await expect(dialog).toContainText(`main.${table} is ready`, { timeout: 60_000 });
    await expect(dialog).toContainText('3 rows imported');
    await expect(dialog).toContainText('region VARCHAR');
    await expect(dialog).toContainText('units BIGINT');
    await expect(dialog).toContainText('revenue DOUBLE');

    await dialog.getByRole('button', { name: 'Done' }).click();
    await expect(dialog).toBeHidden();

    // Reading it back through ordinary SQL is the only proof that matters. A dialog that reports
    // success while the rows went nowhere looks identical from inside the dialog.
    await returnToEditor(page);
    await page
      .getByLabel('SQL editor')
      .fill(`SELECT region, units, revenue FROM main.${table} ORDER BY units;`);
    await page.getByRole('main').getByRole('button', { name: /^Run/ }).click();

    await expect(page.getByRole('columnheader', { name: /region/i })).toBeVisible();
    await expect(page.getByRole('main')).toContainText('north');
    await expect(page.getByRole('main')).toContainText('south');
    await expect(page.getByRole('main')).toContainText('east');
    await expect(page.getByRole('main')).toContainText('3 rows');
  });

  test('refuses to overwrite an existing table', async ({ page, request }) => {
    const table = uniqueTable();
    const token = await issueOwnerToken(request, `e2e-import-seed-${Date.now()}`);
    await request.post(`/api/tenants/${testTenant}/catalogs/analytics/query`, {
      headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
      data: { sql: `CREATE TABLE main."${table}" (existing INTEGER)` },
    });

    await page.reload();
    await expect(page.getByLabel('SQL editor')).toBeVisible();
    await openFileImport(page);
    const dialog = page.getByRole('dialog', { name: 'Import a file as a table' });

    await dialog.locator('input[type="file"]').setInputFiles({
      name: 'regions.csv',
      mimeType: 'text/csv',
      buffer: Buffer.from(csv),
    });
    await dialog.getByLabel('New table').fill(table);
    await dialog.getByRole('button', { name: 'Create table' }).click();

    // Importing over an existing table must fail loudly. Silently replacing it would destroy data
    // the person never mentioned, from a dialog whose stated job is to *add* data.
    await expect(dialog).toContainText(/exists|already|failed/i);

    // And the original table is untouched: its column is still there and the CSV's are not.
    await page.getByRole('button', { name: 'Close file import' }).click();
    await returnToEditor(page);
    await page.getByLabel('SQL editor').fill(`SELECT * FROM main.${table};`);
    await page.getByRole('main').getByRole('button', { name: /^Run/ }).click();
    await expect(page.getByRole('columnheader', { name: /existing/i })).toBeVisible();
    await expect(page.getByRole('columnheader', { name: /region/i })).toBeHidden();
  });
});
