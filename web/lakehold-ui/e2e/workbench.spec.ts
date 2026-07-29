import { expect, test, type Locator, type Page } from '@playwright/test';

async function openWorkbench(page: Page): Promise<void> {
  const tenants = page.waitForResponse(
    (response) => response.url().endsWith('/api/tenants') && response.request().method() === 'GET',
  );
  await page.goto('/workbench');
  expect((await tenants).ok()).toBe(true);
}

async function expectOffCanvas(locator: Locator): Promise<void> {
  await expect(locator).toHaveAttribute('aria-hidden', 'true');
  await expect(locator).toHaveAttribute('inert', '');
  await expect
    .poll(async () => {
      const box = await locator.boundingBox();
      return box ? box.x + box.width : 1;
    })
    .toBeLessThanOrEqual(0);
}

test.describe('workbench user journeys', () => {
  test.beforeEach(async ({ page }) => {
    await openWorkbench(page);
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
    const navigation = page.locator('#workbench-navigation');
    const toggle = page.getByRole('button', { name: 'Collapse navigation' });
    const filter = page.getByLabel('Filter catalog objects');
    const main = page.locator('main');

    await filter.fill('events');
    await expect(navigation).toBeVisible();
    await expect(navigation).toHaveAttribute('aria-hidden', 'false');
    await expect(navigation).not.toHaveAttribute('inert', '');
    const expandedMain = await main.boundingBox();
    expect(expandedMain).not.toBeNull();

    await toggle.click();
    await expect(navigation).toBeHidden();
    await expect(navigation).toHaveAttribute('aria-hidden', 'true');
    await expect(navigation).toHaveAttribute('inert', '');
    await expect(page.getByRole('button', { name: 'Expand navigation' })).toHaveAttribute(
      'aria-expanded',
      'false',
    );
    await expect
      .poll(async () => (await main.boundingBox())?.x)
      .toBeLessThan(expandedMain!.x - 200);
    await expect
      .poll(async () => (await main.boundingBox())?.width)
      .toBeGreaterThan(expandedMain!.width + 200);

    await page.getByRole('button', { name: 'Expand navigation' }).click();
    await expect(navigation).toBeVisible();
    await expect(navigation).toHaveAttribute('aria-hidden', 'false');
    await expect(navigation).not.toHaveAttribute('inert', '');
    await expect(filter).toHaveValue('events');
    await expect.poll(async () => (await main.boundingBox())?.x).toBeCloseTo(expandedMain!.x, 0);
    await expect
      .poll(async () => (await main.boundingBox())?.width)
      .toBeCloseTo(expandedMain!.width, 0);
  });

  test('restores focus when both desktop navigation surfaces are collapsed', async ({ page }) => {
    const navigation = page.locator('#workbench-navigation');
    const contextPanel = page.locator('#workbench-context-panel');

    await page.getByRole('button', { name: 'Collapse navigation' }).click();
    await page.getByRole('button', { name: 'Collapse catalog panel' }).click();

    await expect(navigation).toHaveAttribute('inert', '');
    await expect(contextPanel).toHaveAttribute('inert', '');
    await expect(page.getByRole('button', { name: 'Expand navigation' })).toBeFocused();
  });

  test('routes every product-navigation destination to its existing workbench surface', async ({
    page,
  }) => {
    const navigation = page.locator('#workbench-navigation');
    const activeTab = page.locator('main .tabs .tab.active');
    const panelDestinations = [
      ['Query history', 'Query history'],
      ['Data history', 'Data history'],
      ['Storage', 'Storage'],
      ['Changes', 'Changes'],
      ['Backups', 'Backups'],
      ['Eject', 'Eject'],
      ['Schedule', 'Schedule'],
      ['Workbench', 'Results'],
    ] as const;

    for (const [destination, tab] of panelDestinations) {
      const button = navigation.getByRole('button', { name: destination, exact: true });
      await button.click();
      await expect(button).toHaveClass(/active/);
      await expect(button).toHaveAttribute('aria-current', 'page');
      await expect(activeTab).toHaveText(tab);
    }

    const savedQueries = navigation.getByRole('button', {
      name: 'Saved queries',
      exact: true,
    });
    await savedQueries.click();
    await expect(savedQueries).toHaveClass(/active/);
    await expect(savedQueries).toHaveAttribute('aria-current', 'page');
    await expect(page.locator('lh-saved-queries-panel')).toBeVisible();

    const catalog = navigation.getByRole('button', { name: 'Catalog', exact: true });
    await catalog.click();
    await expect(catalog).toHaveClass(/active/);
    await expect(catalog).toHaveAttribute('aria-current', 'page');
    await expect(page.getByLabel('Filter catalog objects')).toBeVisible();
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
    await openWorkbench(page);

    const navigation = page.locator('#workbench-navigation');
    const backdrop = page.locator('.nav-backdrop');
    const toggle = page.getByRole('button', { name: 'Expand navigation' });
    const main = page.locator('main');
    const topbar = page.locator('.topbar');
    await expectOffCanvas(navigation);
    await expect(backdrop).not.toHaveClass(/visible/);
    await expect(backdrop).toHaveAttribute('aria-hidden', 'true');
    await expect(backdrop).toHaveAttribute('inert', '');
    await expect(backdrop).toBeDisabled();

    await toggle.click();
    await expect(navigation).toHaveAttribute('aria-hidden', 'false');
    await expect(navigation).not.toHaveAttribute('inert', '');
    await expect.poll(async () => (await navigation.boundingBox())?.x).toBeCloseTo(0, 0);
    await expect(backdrop).toHaveClass(/visible/);
    await expect(backdrop).not.toHaveAttribute('aria-hidden', 'true');
    await expect(backdrop).not.toHaveAttribute('inert', '');
    await expect(backdrop).toBeEnabled();
    await expect(main).toHaveAttribute('aria-hidden', 'true');
    await expect(main).toHaveAttribute('inert', '');
    await expect(topbar).toHaveAttribute('aria-hidden', 'true');
    await expect(topbar).toHaveAttribute('inert', '');
    await expect(navigation.getByRole('button', { name: 'Workbench', exact: true })).toBeFocused();
    await page.keyboard.press('Escape');

    await expectOffCanvas(navigation);
    await expect(backdrop).not.toHaveClass(/visible/);
    await expect(backdrop).toHaveAttribute('aria-hidden', 'true');
    await expect(main).not.toHaveAttribute('aria-hidden', 'true');
    await expect(main).not.toHaveAttribute('inert', '');
    await expect(topbar).not.toHaveAttribute('aria-hidden', 'true');
    await expect(topbar).not.toHaveAttribute('inert', '');
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await expect(toggle).toBeFocused();
  });

  test('hands off mobile navigation to panels and dismisses contextual navigation by backdrop', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 760, height: 900 });
    await openWorkbench(page);

    const navigation = page.locator('#workbench-navigation');
    const backdrop = page.locator('.nav-backdrop');
    const contextPanel = page.locator('aside[aria-label="Catalog and saved queries"]');
    const toggle = page.locator('.nav-toggle');
    const main = page.locator('main');
    const topbar = page.locator('.topbar');

    await toggle.click();
    await navigation.getByRole('button', { name: 'Storage', exact: true }).click();

    await expect(navigation).toHaveAttribute('aria-hidden', 'true');
    await expect(navigation).toHaveAttribute('inert', '');
    await expect(backdrop).not.toHaveClass(/visible/);
    await expect(page.locator('main .tabs .tab.active')).toHaveText('Storage');
    await expect(page.getByRole('main')).toBeVisible();

    await page.getByRole('button', { name: 'Expand navigation' }).click();
    await navigation.getByRole('button', { name: 'Catalog', exact: true }).click();

    await expect(navigation).toHaveAttribute('aria-hidden', 'true');
    await expect(contextPanel).toHaveAttribute('aria-hidden', 'false');
    await expect(contextPanel).not.toHaveAttribute('inert', '');
    await expect.poll(async () => (await contextPanel.boundingBox())?.x).toBeCloseTo(0, 0);
    await expect(page.getByLabel('Filter catalog objects')).toBeVisible();
    await expect(backdrop).toHaveClass(/visible/);
    await expect(contextPanel.locator('.sidebar-tab.active')).toBeFocused();
    await expect(main).toHaveAttribute('inert', '');
    await expect(topbar).toHaveAttribute('inert', '');

    await backdrop.click();
    await expectOffCanvas(contextPanel);
    await expect(backdrop).not.toHaveClass(/visible/);
    await expect(toggle).toBeFocused();
    await expect(main).not.toHaveAttribute('inert', '');
    await expect(topbar).not.toHaveAttribute('inert', '');
  });

  test('scrolls the navigation rail to reach operations on a short viewport', async ({ page }) => {
    await page.setViewportSize({ width: 760, height: 375 });
    await openWorkbench(page);

    const navigation = page.locator('#workbench-navigation');
    await page.getByRole('button', { name: 'Expand navigation' }).click();

    const schedule = navigation.locator('.nav-item', { hasText: /^Schedule$/ });
    await schedule.scrollIntoViewIfNeeded();
    await expect(schedule).toBeInViewport();
    await expect.poll(() => navigation.evaluate((element) => element.scrollTop)).toBeGreaterThan(0);

    await schedule.click();
    await expect(schedule).toHaveAttribute('aria-current', 'page');
    await expect(page.locator('main .tabs .tab.active')).toHaveText('Schedule');
    await expectOffCanvas(navigation);
  });

  test('resets both navigation surfaces when crossing the compact breakpoint', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await openWorkbench(page);

    const navigation = page.locator('#workbench-navigation');
    const contextPanel = page.locator('aside[aria-label="Catalog and saved queries"]');
    const catalogFilter = page.getByLabel('Filter catalog objects');

    await expect(navigation).toBeVisible();
    await expect(contextPanel).toBeVisible();
    await catalogFilter.focus();

    await page.setViewportSize({ width: 760, height: 900 });
    await expectOffCanvas(navigation);
    await expectOffCanvas(contextPanel);
    const toggle = page.getByRole('button', { name: 'Expand navigation' });
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await expect(toggle).toBeFocused();

    await page.setViewportSize({ width: 1280, height: 900 });
    await expect(navigation).toBeVisible();
    await expect(navigation).toHaveAttribute('aria-hidden', 'false');
    await expect(navigation).not.toHaveAttribute('inert', '');
    await expect(contextPanel).toBeVisible();
    await expect(contextPanel).toHaveAttribute('aria-hidden', 'false');
    await expect(contextPanel).not.toHaveAttribute('inert', '');
    await expect(page.getByRole('button', { name: 'Collapse navigation' })).toHaveAttribute(
      'aria-expanded',
      'true',
    );
  });
});
