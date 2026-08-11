import { expect, test } from '@playwright/test';

const pages = [
  {
    path: '/',
    heading: 'LakeHold: an Open Source Enterprise LakeHouse, you host yourself',
    title: /LakeHold/,
  },
  {
    path: '/enterprise-data-platform',
    heading: 'LakeHold as an Enterprise Data Platform',
    title: /Enterprise Data Platform/,
  },
  { path: '/compare', heading: /Compare|LakeHold/, title: /LakeHold vs/ },
  { path: '/docs', heading: /LakeHold/, title: /Documentation/ },
  {
    path: '/docs/linq-workbench',
    heading: 'C# LINQ in the Workbench',
    title: /C# LINQ Workbench/,
  },
  {
    path: '/docs/connectors',
    heading: 'Managed data connectors',
    title: /Managed data connectors/,
  },
  {
    path: '/docs/enterprise-data-platform-roadmap',
    heading: 'Enterprise Data Platform delivery plan',
    title: /Enterprise Data Platform delivery plan/,
  },
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
      page.getByRole('heading', {
        name: 'LakeHold: an Open Source Enterprise LakeHouse, you host yourself',
      }),
    ).toBeVisible();
  });

  test('the mobile landing stays compact without horizontal overflow', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto('/');

    const header = await page.locator('.nav').boundingBox();
    const topology = await page.locator('.topology').boundingBox();

    expect(header).not.toBeNull();
    expect(topology).not.toBeNull();
    expect(header!.height).toBeLessThanOrEqual(96);
    expect(topology!.height).toBeLessThanOrEqual(800);
    await expect(page.locator('lh-landing')).toHaveJSProperty('scrollLeft', 0);

    const hasHorizontalOverflow = await page.locator('lh-landing').evaluate((landing) => {
      return landing.scrollWidth > landing.clientWidth;
    });
    expect(hasHorizontalOverflow).toBe(false);
  });
});
