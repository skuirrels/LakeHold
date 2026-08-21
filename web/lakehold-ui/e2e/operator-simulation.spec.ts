import { expect, test } from '@playwright/test';
import { signIn } from './credential';
import { openDetailSection } from './support/table-detail';

test('operator simulation exercises the lakehouse control surfaces', async ({ page, request }) => {
  await signIn(page, request);
  const tenants = page.waitForResponse(
    (response) => response.url().endsWith('/api/tenants') && response.request().method() === 'GET',
  );
  await page.goto('/workbench');
  expect((await tenants).ok()).toBe(true);
  await expect(page.locator('.selectors select').nth(0)).toHaveValue('demo');
  await expect(page.locator('.selectors select').nth(1)).toHaveValue('analytics');
  const navigation = page.locator('#workbench-navigation');

  await test.step('inspect the physical files behind a table', async () => {
    await navigation.getByRole('button', { name: 'Storage', exact: true }).click();
    await page.getByRole('button', { name: 'Show data files for main.events' }).click();

    await expect(page.getByRole('heading', { name: /events/ })).toBeVisible();
    await expect(page.getByText('Partition layout')).toBeVisible();
    await openDetailSection(page, 'Files');
    await expect(page.locator('lh-table-detail')).toContainText(/parquet|No Parquet files/i);

    await openDetailSection(page, 'Columns');
    await expect(page.getByText(/live rows/)).toBeVisible();
    await page.locator('.profiles button.cell-link').first().click();
    await expect(page.getByRole('heading', { name: /distribution/ })).toBeVisible();
    await page.getByRole('button', { name: 'Close table detail' }).click();
  });

  await test.step('read the catalog change feed', async () => {
    await navigation.getByRole('button', { name: 'Changes', exact: true }).click();
    await page.getByRole('button', { name: 'Read changes' }).click();

    await expect(page.locator('.panel-summary')).toContainText(/snapshots \d+–\d+/);
    await expect(page.locator('.panel-summary')).toContainText(/change/);
    await expect(page.getByRole('heading', { name: 'Webhook subscriptions' })).toBeVisible();
  });

  await test.step('review and cancel an atomic snapshot restore without applying it', async () => {
    await navigation.getByRole('button', { name: 'Data history', exact: true }).click();
    await page
      .getByRole('button', { name: /Review restore/ })
      .first()
      .click();

    await expect(page.getByRole('region', { name: 'Restore plan' })).toBeVisible();
    await expect(page.getByText(/current table definition.*stay in place/i)).toBeVisible();
    await page.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(page.getByRole('region', { name: 'Restore plan' })).not.toBeVisible();
  });

  await test.step('flush safely and create a catalog backup', async () => {
    await navigation.getByRole('button', { name: 'Workbench', exact: true }).click();
    await page.getByRole('button', { name: 'Maintain' }).click();
    await page.getByRole('button', { name: 'Flush' }).click();
    await expect(page.locator('.banner.ok-banner')).toContainText(/flush/i);

    await page.getByRole('button', { name: 'Maintain' }).click();
    await page
      .locator('.maintenance-popover')
      .getByRole('button', { name: /^Backup/ })
      .click();
    await expect(page.locator('.banner.ok-banner')).toContainText(/backup/i);
    await navigation.getByRole('button', { name: 'Backups', exact: true }).click();

    await expect(page.getByRole('columnheader', { name: 'Generation' })).toBeVisible();
    await expect(page.locator('tbody tr').first()).toContainText(/\d{8}T\d{6}Z/);
    await expect(page.getByRole('button', { name: 'Restore…' }).first()).toBeVisible();
  });

  await test.step('create and inspect a verified open-format eject', async () => {
    await navigation.getByRole('button', { name: 'Eject', exact: true }).click();
    await page.getByRole('button', { name: 'Eject now' }).click();

    await expect(page.locator('lh-eject-panel .ok-banner')).toContainText('Verified.', {
      timeout: 30_000,
    });
    await expect(page.getByRole('columnheader', { name: 'Bundle' })).toBeVisible();
    await page.locator('button.cell-link').first().click();
    await expect(page.getByRole('columnheader', { name: 'SHA-256' })).toBeVisible();
    await expect(page.locator('.bundle-detail tbody tr').first()).toBeVisible();
  });

  await test.step('inspect scheduled-operation visibility', async () => {
    await navigation.getByRole('button', { name: 'Schedule', exact: true }).click();

    await expect(
      page
        .getByText(/No scheduled runs recorded yet/)
        .or(page.getByRole('columnheader', { name: 'Job' })),
    ).toBeVisible();
  });
});
