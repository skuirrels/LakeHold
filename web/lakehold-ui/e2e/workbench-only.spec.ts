import { expect, test } from '@playwright/test';

test.describe('private production surface', () => {
  test.skip(
    !process.env['LAKEHOLD_WORKBENCH_ONLY'],
    'runs only against the production Workbench configuration',
  );

  test('exposes the Workbench without exposing the public website', async ({ page, request }) => {
    const root = await request.get('/', { maxRedirects: 0 });
    expect(root.status()).toBe(302);
    expect(root.headers()['location']).toBe('/workbench');

    await page.goto('/');
    await expect(page).toHaveURL(/\/workbench$/);
    await expect(page.locator('lh-workbench')).toBeVisible();

    for (const route of ['/compare', '/docs', '/provider', '/provider/docs']) {
      const response = await request.get(route, { maxRedirects: 0 });
      expect(response.status(), `${route} should not be exposed`).toBe(404);
    }
  });
});
