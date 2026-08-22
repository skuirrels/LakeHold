import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { issueOwnerToken, signIn, testTenant } from './credential';

/** Every name this suite creates starts here, so cleanup can find them without tracking ids. */
const namePrefix = 'journey_';

/**
 * Removes everything the suite created, including from runs that failed part-way through.
 *
 * Publication writes a real DuckLake view, so a test that dies between publishing and unpublishing
 * leaves an object behind in a catalog the whole suite shares. Deleting by name prefix rather than
 * by the ids of this run also clears leftovers from an earlier crashed run, which is what stops the
 * seeded catalog accumulating debris until an unrelated test starts failing on it.
 */
async function removeJourneyQueries(request: APIRequestContext): Promise<void> {
  const token = await issueOwnerToken(request, `e2e-cleanup-${Date.now()}`);
  const headers = { Authorization: `Bearer ${token}` };
  const base = `/api/tenants/${testTenant}/catalogs/analytics/saved-queries`;

  const listed = await request.get(base, { headers });
  if (!listed.ok()) {
    return;
  }

  const queries = (await listed.json()) as {
    id: number;
    name: string;
    revision: number;
    publishedViewName?: string | null;
  }[];

  for (const query of queries.filter((candidate) => candidate.name.startsWith(namePrefix))) {
    // Both routes take the revision the caller believes it is acting on, so an edit that landed
    // between listing and deleting is refused rather than silently overwritten. Unpublish bumps it,
    // which is why the delete uses the revision that call returns rather than the listed one.
    let revision = query.revision;
    if (query.publishedViewName) {
      const dropped = await request.post(`${base}/${query.id}/unpublish?revision=${revision}`, {
        headers,
      });
      if (dropped.ok()) {
        revision = ((await dropped.json()) as { revision: number }).revision;
      }
    }

    await request.delete(`${base}/${query.id}?revision=${revision}`, { headers });
  }

  // A view can outlive its record: if a run dies between publishing and unpublishing and the record
  // is removed first, nothing left in the API can name the object any more. Sweeping the catalog by
  // prefix is the only way to make cleanup self-healing rather than merely tidy.
  const query = `/api/tenants/${testTenant}/catalogs/analytics/query`;
  const orphans = await request.post(query, {
    headers: { ...headers, 'Content-Type': 'application/json' },
    data: {
      sql: `SELECT view_name FROM duckdb_views() WHERE NOT internal AND view_name LIKE '${namePrefix}%'`,
    },
  });
  if (!orphans.ok()) {
    return;
  }

  const { rows } = (await orphans.json()) as { rows: string[][] };
  for (const [view] of rows) {
    await request.post(query, {
      headers: { ...headers, 'Content-Type': 'application/json' },
      data: { sql: `DROP VIEW IF EXISTS main."${view}"` },
    });
  }
}

/**
 * The reusable-saved-query lifecycle, driven the way a person drives it.
 *
 * The panel had one browser assertion — that clicking its navigation entry made it visible — while
 * the component behind it exposes create, edit, run, publish, republish, unpublish, and delete.
 * Publication is the part worth a real stack: it writes a DuckLake view, so the only honest proof
 * that it worked is the view appearing in the catalog and answering a query.
 *
 * Each test mints its own query name. The suite runs against a shared seeded catalog, so a fixed
 * name would collide with a previous run's leftovers and fail for a reason that has nothing to do
 * with the behaviour under test.
 */
function uniqueName(suffix: string): string {
  return `${namePrefix}${suffix}_${Date.now().toString(36)}${Math.floor(Math.random() * 1e4).toString(36)}`;
}

async function openWorkbench(page: Page): Promise<void> {
  const tenants = page.waitForResponse(
    (response) => response.url().endsWith('/api/tenants') && response.request().method() === 'GET',
  );
  await page.goto('/workbench');
  expect((await tenants).ok()).toBe(true);
  await expect(page.getByLabel('SQL editor')).toBeVisible();
}

/** Replaces the editor contents. The editor is a contenteditable, so `fill` is the reliable path. */
async function writeSql(page: Page, sql: string): Promise<void> {
  const editor = page.getByLabel('SQL editor');
  await editor.click();
  await editor.fill(sql);
}

/** Opens the saved-query panel and returns its root, which every later locator is scoped to. */
async function openSavedQueries(page: Page) {
  await page
    .locator('#workbench-navigation')
    .getByRole('button', { name: 'Query library', exact: true })
    .click();
  const panel = page.locator('lh-saved-queries-panel');
  await expect(panel).toBeVisible();
  return panel;
}

test.describe('saved query lifecycle', () => {
  test.beforeEach(async ({ page, request }) => {
    await signIn(page, request);
    await openWorkbench(page);
  });

  test.afterEach(async ({ request }) => {
    await removeJourneyQueries(request);
  });

  test('saves the current statement and runs it back from the panel', async ({ page }) => {
    const name = uniqueName('reuse');
    await writeSql(page, "SELECT 'saved-query-journey' AS marker, 21 * 2 AS answer;");

    const panel = await openSavedQueries(page);
    await panel.getByRole('button', { name: 'Save current' }).click();
    await panel.locator('.query-form').getByLabel('Name', { exact: true }).fill(name);
    await panel.getByLabel('Description').fill('Created by the saved-query browser journey.');
    await panel.getByRole('button', { name: 'Save', exact: true }).click();

    // The definition is durable, not just rendered: it survives a reload of the whole application.
    await expect(panel.getByText(name)).toBeVisible();
    await page.reload();
    const reopened = await openSavedQueries(page);
    const entry = reopened.locator('li', { hasText: name });
    await expect(entry).toBeVisible();

    // Running from the panel must put the saved source into the editor and produce its result,
    // which is what makes a saved query reusable rather than a bookmark.
    await entry.getByRole('button', { name: 'Run', exact: true }).click();
    await expect(page.getByRole('columnheader', { name: /marker/i })).toBeVisible();
    await expect(page.getByRole('columnheader', { name: /answer/i })).toBeVisible();
    await expect(page.getByRole('main')).toContainText('saved-query-journey');
    await expect(page.getByRole('main')).toContainText('42');
  });

  test('publishes a saved query as a catalog view and drops it again', async ({ page }) => {
    const name = uniqueName('view');
    await writeSql(page, "SELECT 'published' AS state, 7 AS magnitude;");

    const panel = await openSavedQueries(page);
    await panel.getByRole('button', { name: 'Save current' }).click();
    await panel.locator('.query-form').getByLabel('Name', { exact: true }).fill(name);
    await panel.getByRole('button', { name: 'Save', exact: true }).click();

    const entry = panel.locator('li', { hasText: name });
    await expect(entry).toBeVisible();

    await entry.getByRole('button', { name: 'Publish', exact: true }).click();
    await panel.getByRole('button', { name: 'Publish view' }).click();

    // Asserted on the control, not the word "published": the badge, the row label and — before this
    // was fixed — the generated name all contain that substring, so matching it passed before the
    // request had landed and the test raced on into a half-open publish form.
    await expect(entry).toContainText('Unpublish');

    await page
      .locator('#workbench-navigation')
      .getByRole('button', { name: 'Catalog', exact: true })
      .click();
    // Polled rather than filtered, because the explorer loads asynchronously when it is shown and
    // typing into the filter races that load. Note what this does and does not prove: it asserts the
    // published view is visible to a person who opens the catalog, not that any particular refresh
    // path fired — removing the panel's `schemaChanged` wiring leaves this test green, because
    // switching to the tab reloads the explorer anyway.
    await expect
      .poll(async () => (await page.locator('main').innerText()).includes(name), {
        timeout: 15_000,
      })
      .toBe(true);

    await page
      .locator('#workbench-navigation')
      .getByRole('button', { name: 'Workbench', exact: true })
      .click();
    await writeSql(page, `SELECT state, magnitude FROM main.${name};`);
    await page.getByRole('main').getByRole('button', { name: /^Run/ }).click();
    await expect(page.getByRole('main')).toContainText('published');

    // Unpublish drops the view. Asking the catalog for it afterwards must fail, because a view that
    // still answers after being dropped would mean the panel and the catalog disagree.
    const reopened = await openSavedQueries(page);
    const republished = reopened.locator('li', { hasText: name });
    await expect(republished).toContainText('Unpublish');
    await republished.getByRole('button', { name: 'Unpublish' }).click();
    await republished.getByRole('button', { name: 'Drop view' }).click();
    await expect(republished).not.toContainText('Unpublish');

    await page
      .locator('#workbench-navigation')
      .getByRole('button', { name: 'Workbench', exact: true })
      .click();
    await writeSql(page, `SELECT * FROM main.${name};`);
    await page.getByRole('main').getByRole('button', { name: /^Run/ }).click();
    await expect(page.locator('.error-banner')).toContainText(new RegExp(name, 'i'));
  });

  test('flags a published view as stale once its source changes', async ({ page }) => {
    const name = uniqueName('drift');
    await writeSql(page, 'SELECT 1 AS version;');

    const panel = await openSavedQueries(page);
    await panel.getByRole('button', { name: 'Save current' }).click();
    await panel.locator('.query-form').getByLabel('Name', { exact: true }).fill(name);
    await panel.getByRole('button', { name: 'Save', exact: true }).click();

    const entry = panel.locator('li', { hasText: name });
    await entry.getByRole('button', { name: 'Publish', exact: true }).click();
    await panel.getByRole('button', { name: 'Publish view' }).click();
    await expect(entry).toContainText('Unpublish');

    // Move the source out from under the published view: a new revision is saved from whatever the
    // editor currently holds, so the editor changes first and `Save revision` captures it. The badge
    // exists so that a reader querying the view is not silently served the superseded definition
    // with nothing anywhere saying so.
    await page
      .locator('#workbench-navigation')
      .getByRole('button', { name: 'Workbench', exact: true })
      .click();
    await writeSql(page, 'SELECT 2 AS version;');
    await openSavedQueries(page);
    await entry.getByRole('button', { name: 'Edit' }).click();
    await panel.getByRole('button', { name: 'Save revision' }).click();

    await expect(entry).toContainText('Needs attention · republish');

    // Republishing clears it, which is the other half of the claim — the badge tracks the source
    // rather than latching on once.
    await entry.getByRole('button', { name: 'Republish', exact: true }).click();
    // Scoped to the form: the row's trigger and the form's submit share the label, and picking the
    // wrong one silently re-opens the form instead of republishing.
    await panel.locator('.publish-form').getByRole('button', { name: 'Republish' }).click();
    await expect(entry).not.toContainText('Needs attention · republish');
    await expect(entry).toContainText('Unpublish');

    await entry.getByRole('button', { name: 'Unpublish' }).click();
    await entry.getByRole('button', { name: 'Drop view' }).click();
  });
});
