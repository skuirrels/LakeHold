import { expect, test } from '@playwright/test';

test.describe('demo workbench', () => {
  test.skip(!process.env['LAKEHOLD_DEMO'], 'runs only against the disposable demo stack');

  test('@demo opens directly as a safe, useful visitor experience', async ({ page, request }) => {
    const baseURL = process.env['LAKEHOLD_E2E_BASE_URL'] ?? 'http://127.0.0.1:6599';

    await page.goto('/');
    await expect(page).toHaveURL(/\/$/);
    await expect(
      page.getByRole('heading', {
        name: 'A feature-rich lakehouse. You host it yourself.',
      }),
    ).toBeVisible();

    await page.goto('/workbench');

    await expect(page.getByText('You’re exploring a live LakeHold demo')).toBeVisible();
    await expect(page.getByLabel('Workspace')).toHaveValue('demo');
    await expect(page.locator('.selectors').getByLabel('Catalog')).toHaveValue('analytics');
    await expect(page.getByLabel('SQL editor')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Operator sign in' })).toBeVisible();
    await expect(page.getByText('This deployment requires a credential')).toHaveCount(0);
    await expect(page.getByText('docker compose')).toHaveCount(0);

    await page.getByRole('button', { name: /Run/ }).click();
    await expect(page.getByText(/rows · .* ms/)).toBeVisible();

    for (const operation of ['Flush', 'Compact', 'Backup', 'Expire', 'Cleanup']) {
      await expect(page.getByRole('button', { name: operation, exact: true })).toHaveCount(0);
    }

    await page.getByRole('main').getByRole('button', { name: 'Changes', exact: true }).click();
    await expect(page.getByRole('button', { name: 'New subscription' })).toHaveCount(0);

    await page.getByRole('main').getByRole('button', { name: 'Eject', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Eject now' })).toHaveCount(0);

    const access = await request.get(`${baseURL}/api/access`);
    expect(access.status()).toBe(200);
    expect(await access.json()).toEqual({
      mode: 'demo',
      role: 'reader',
      readOnly: true,
    });

    const visible = await request.get(`${baseURL}/api/tenants`);
    expect(visible.status()).toBe(200);
    expect(await visible.json()).toEqual([
      {
        slug: 'demo',
        displayName: 'Demo workspace',
        catalogs: [
          {
            name: 'analytics',
            dataPath: '/var/lib/lakehold/data/demo/analytics',
            isReadOnly: false,
            metadataKind: 'Postgres',
            storageKind: 'Local',
            storageProfile: null,
          },
        ],
      },
    ]);

    const write = await request.post(`${baseURL}/api/tenants/demo/catalogs/analytics/query`, {
      data: { sql: 'CREATE TABLE public_visitors_must_not_write (id INTEGER)' },
    });
    expect(write.status()).toBe(400);
    expect(await write.text()).toMatch(/read.only/i);

    const maintenance = await request.post(
      `${baseURL}/api/tenants/demo/catalogs/analytics/maintenance/cleanup`,
    );
    expect(maintenance.status()).toBe(403);

    const subscription = await request.post(
      `${baseURL}/api/tenants/demo/catalogs/analytics/subscriptions`,
      {
        data: {
          endpointUrl: 'https://example.invalid/lakehold',
          secret: 'must-not-be-created',
        },
      },
    );
    expect(subscription.status()).toBe(403);

    const crossCatalog = await request.get(`${baseURL}/api/tenants/demo/catalogs/private/schemas`);
    expect(crossCatalog.status()).toBe(404);
  });
});
