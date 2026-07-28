import { expect, test } from '@playwright/test';

const pages = [
  { path: '/', heading: 'A feature-rich lakehouse. You host it yourself.', title: /LakeHold/ },
  { path: '/compare', heading: /Compare|LakeHold/, title: /LakeHold vs/ },
  { path: '/docs', heading: /LakeHold/, title: /Documentation/ },
  { path: '/docs/operations', heading: 'Operating LakeHold', title: /Operations/ },
  {
    path: '/docs/incident-response',
    heading: 'Incident-response runbook',
    title: /Incident response/,
  },
  {
    path: '/docs/disaster-recovery',
    heading: 'Disaster-recovery runbook',
    title: /Disaster recovery/,
  },
  {
    path: '/docs/monitoring',
    heading: 'Monitoring and alerting',
    title: /Monitoring and alerting/,
  },
  {
    path: '/provider',
    heading: /DuckDB, DuckLake & Parquet, Provider for \.NET\./,
    title: /DuckDB\.EFCoreProvider/,
  },
  { path: '/provider/docs', heading: /DuckDB\.EFCoreProvider/, title: /documentation/i },
];

test.describe('@website public product pages', () => {
  for (const page of pages) {
    test(`${page.path} renders its primary content`, async ({ page: browser }) => {
      const response = await browser.goto(page.path);

      expect(response?.ok()).toBe(true);
      await expect(browser).toHaveTitle(page.title);
      await expect(browser.getByRole('heading', { name: page.heading }).first()).toBeVisible();
    });
  }

  test('the sitemap lists every public product and documentation route', async ({ request }) => {
    const response = await request.get('/sitemap.xml');

    expect(response.ok()).toBe(true);
    const sitemap = await response.text();
    for (const page of pages) {
      expect(sitemap, page.path).toContain(
        `<loc>https://lakehold.dev${page.path === '/' ? '/' : page.path}</loc>`,
      );
    }
  });

  test('an unknown route returns the user to the landing page', async ({ page }) => {
    await page.goto('/this-route-does-not-exist');

    await expect(page).toHaveURL(/\/$/);
    await expect(
      page.getByRole('heading', { name: 'A feature-rich lakehouse. You host it yourself.' }),
    ).toBeVisible();
  });
});
