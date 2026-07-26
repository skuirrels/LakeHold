import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { expect, test } from '@playwright/test';
import { compareCapabilities } from './support/compare-capabilities';

const repoRoot = resolve(process.cwd(), '../..');

test.describe('@website /compare capability contract', () => {
  test('every rendered LakeHold claim has current evidence', async ({ page }) => {
    await page.goto('/compare');

    const rendered = await page.locator('.matrix tbody tr').evaluateAll((rows) =>
      rows.map((row) => {
        const cells = row.querySelectorAll('th, td');
        const lakehold = cells[1] as HTMLElement | undefined;
        return {
          dimension: cells[0]?.textContent?.trim() ?? '',
          claim: lakehold?.textContent?.trim() ?? '',
          tone:
            [...(lakehold?.classList ?? [])]
              .find((name) => name.startsWith('v-'))
              ?.replace('v-', '') ?? '',
        };
      }),
    );

    expect(rendered).toEqual(
      compareCapabilities.map(({ dimension, claim, tone }) => ({ dimension, claim, tone })),
    );

    for (const capability of compareCapabilities) {
      expect(
        capability.evidence.length,
        `${capability.dimension} must name at least one proof lane`,
      ).toBeGreaterThan(0);

      for (const evidence of capability.evidence) {
        const evidencePath = resolve(repoRoot, evidence.path);
        expect(existsSync(evidencePath), `${capability.dimension}: missing ${evidence.path}`).toBe(
          true,
        );
        expect(
          readFileSync(evidencePath, 'utf8'),
          `${capability.dimension}: "${evidence.marker}" is stale in ${evidence.path}`,
        ).toContain(evidence.marker);
      }
    }
  });

  test('decision guidance, limitations, objection, and workbench handoff stay usable', async ({
    page,
  }) => {
    await page.goto('/compare');

    for (const competitor of ['MotherDuck', 'ClickHouse', 'Snowflake / Databricks']) {
      const section = page.locator('section.head2head').filter({
        has: page.getByRole('heading', { name: `vs ${competitor}` }),
      });
      await expect(section).toBeVisible();
      await expect(section.getByRole('heading', { name: 'Choose LakeHold when' })).toBeVisible();
      await expect(section.locator('article.win li').first()).toBeVisible();
      await expect(section.locator('article.lose li').first()).toBeVisible();
    }

    const objection = page.locator('section.objection');
    await expect(
      objection.getByRole('heading', { name: '“Why not just use DuckDB?”' }),
    ).toBeVisible();
    await expect(objection.locator('li')).toHaveCount(4);

    await page.getByRole('link', { name: 'Open the workbench', exact: true }).click();
    await expect(page).toHaveURL(/\/workbench$/);
    await expect(page.getByLabel('SQL editor')).toBeVisible();
  });
});
