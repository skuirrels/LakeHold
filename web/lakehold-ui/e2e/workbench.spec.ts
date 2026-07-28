import { expect, test } from '@playwright/test';

test.describe('workbench user journeys', () => {
  test.beforeEach(async ({ page }) => {
    const tenants = page.waitForResponse(
      (response) =>
        response.url().endsWith('/api/tenants') && response.request().method() === 'GET',
    );
    await page.goto('/workbench');
    expect((await tenants).ok()).toBe(true);
    await expect(page.getByLabel('SQL editor')).toBeVisible();
    await expect(page.locator('.selectors select').nth(0)).toHaveValue('demo');
    await expect(page.locator('.selectors select').nth(1)).toHaveValue('analytics');
  });

  test('runs a query and renders typed results', async ({ page }) => {
    await page
      .getByLabel('SQL editor')
      .fill("SELECT 42::BIGINT AS answer, 'ready' AS status, NULL::VARCHAR AS optional_value");
    await page.getByRole('button', { name: /^Run/ }).click();

    await expect(page.getByRole('columnheader', { name: /answer/i })).toBeVisible();
    await expect(page.getByRole('cell', { name: '42', exact: true })).toBeVisible();
    await expect(page.getByRole('cell', { name: 'ready', exact: true })).toBeVisible();
    await expect(page.getByRole('cell', { name: 'NULL', exact: true })).toBeVisible();
    await expect(page.locator('.summary')).toContainText('1 row');
  });

  test('collapses and restores navigation without resetting the catalog explorer', async ({
    page,
  }) => {
    const navigation = page.getByRole('navigation', { name: 'Workbench navigation' });
    const toggle = page.getByRole('button', { name: 'Collapse navigation' });
    const filter = page.getByLabel('Filter catalog objects');

    await filter.fill('events');
    await expect(navigation).toBeVisible();

    await toggle.click();
    await expect(navigation).toBeHidden();
    await expect(page.getByRole('button', { name: 'Expand navigation' })).toHaveAttribute(
      'aria-expanded',
      'false',
    );

    await page.getByRole('button', { name: 'Expand navigation' }).click();
    await expect(navigation).toBeVisible();
    await expect(filter).toHaveValue('events');
  });

  test('inserts SQL from the catalog and replays it from history', async ({ page }) => {
    await page.getByLabel('Filter catalog objects').fill('events');
    await page.getByRole('button', { name: 'Insert a SELECT for events' }).click();

    await expect(page.getByLabel('SQL editor')).toHaveValue(/FROM main\.events/);
    await page.getByRole('button', { name: /^Run/ }).click();
    await expect(page.locator('.summary')).toContainText('rows');

    await page.getByRole('main').getByRole('button', { name: 'Query history' }).click();
    const historyRow = page.locator('.history-row').filter({ hasText: 'FROM main.events' }).first();
    await expect(historyRow).toBeVisible();
    await historyRow.click();

    await expect(page.getByLabel('SQL editor')).toHaveValue(/FROM main\.events/);
    await expect(page.getByRole('button', { name: 'Results' })).toHaveClass(/active/);
  });

  test('shows snapshots and storage from the live catalog', async ({ page }) => {
    await page.getByRole('main').getByRole('button', { name: 'Data history' }).click();
    await expect(page.getByRole('columnheader', { name: 'Snapshot' })).toBeVisible();
    await expect(page.locator('table.history-timeline tbody tr').first()).toBeVisible();

    await page.getByRole('main').getByRole('button', { name: 'Storage' }).click();
    await expect(page.getByText('events', { exact: true }).first()).toBeVisible();
    await expect(page.getByText(/Rows|Files/).first()).toBeVisible();
  });

  test('opens table detail and live column profiles from the catalog explorer', async ({
    page,
  }) => {
    await page.getByLabel('Filter catalog objects').fill('events');
    await page.getByRole('button', { name: 'Inspect events' }).click();

    await expect(page.getByRole('main').getByRole('button', { name: 'Storage' })).toHaveClass(
      /active/,
    );
    await expect(page.getByRole('heading', { name: /events/ })).toBeVisible();
    await expect(page.getByText('Partition layout')).toBeVisible();

    await page.getByRole('button', { name: 'Columns', exact: true }).click();
    await expect(page.getByText(/live rows/)).toBeVisible();
    await page.locator('.profiles button.cell-link').first().click();
    await expect(page.getByRole('heading', { name: /distribution/ })).toBeVisible();
  });

  test('keeps destructive maintenance as a cancellable dry run', async ({ page }) => {
    await page.getByRole('button', { name: 'Expire' }).click();

    await expect(page.getByText('Dry run — nothing was changed.')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Apply for real' })).toBeVisible();
    await page.getByRole('button', { name: 'Cancel' }).click();
    await expect(page.getByRole('button', { name: 'Apply for real' })).toBeHidden();
  });

  test('reports invalid SQL and recovers on the next statement', async ({ page }) => {
    await page.getByLabel('SQL editor').fill('SELCT definitely_not_valid');
    await page.getByRole('button', { name: /^Run/ }).click();
    await expect(page.locator('.error-banner')).toContainText(/syntax|parser/i);

    await page.getByLabel('SQL editor').fill('SELECT 1 AS recovered');
    await page.getByRole('button', { name: /^Run/ }).click();
    await expect(page.locator('.error-banner')).toBeHidden();
    await expect(page.getByRole('columnheader', { name: /recovered/i })).toBeVisible();
    await expect(page.locator('tbody tr').getByRole('cell').nth(1)).toHaveText('1');
  });
});

test.describe('responsive workbench navigation', () => {
  test('opens as a compact drawer and closes with Escape', async ({ page }) => {
    await page.setViewportSize({ width: 760, height: 900 });
    const tenants = page.waitForResponse(
      (response) =>
        response.url().endsWith('/api/tenants') && response.request().method() === 'GET',
    );
    await page.goto('/workbench');
    expect((await tenants).ok()).toBe(true);

    const navigation = page.getByRole('navigation', { name: 'Workbench navigation' });
    await expect(navigation).toBeHidden();

    await page.getByRole('button', { name: 'Expand navigation' }).click();
    await expect(navigation).toBeVisible();
    await page.keyboard.press('Escape');

    await expect(navigation).toBeHidden();
    await expect(page.getByRole('button', { name: 'Expand navigation' })).toHaveAttribute(
      'aria-expanded',
      'false',
    );
  });
});
