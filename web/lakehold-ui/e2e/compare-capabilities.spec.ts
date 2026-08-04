import { existsSync, readFileSync } from 'node:fs';
import { signIn } from './credential';
import { resolve } from 'node:path';
import { expect, test } from '@playwright/test';
import { compareCapabilities } from './support/compare-capabilities';

const repoRoot = resolve(process.cwd(), '../..');
const readmeMatrixStart = '<!-- compare-matrix:start -->';
const readmeMatrixEnd = '<!-- compare-matrix:end -->';

interface MatrixRow {
  dimension: string;
  lakehold: string;
  motherduck: string;
  clickhouse: string;
  cloud: string;
}

function readReadmeMatrix(): MatrixRow[] {
  const readme = readFileSync(resolve(repoRoot, 'README.md'), 'utf8');
  const start = readme.indexOf(readmeMatrixStart);
  const end = readme.indexOf(readmeMatrixEnd);

  if (start < 0 || end < 0 || end <= start) {
    throw new Error('README comparison matrix markers are missing or out of order.');
  }

  return readme
    .slice(start + readmeMatrixStart.length, end)
    .split('\n')
    .filter((line) => line.startsWith('|'))
    .slice(2)
    .map((line) => {
      const cells = line
        .slice(1, -1)
        .split('|')
        .map((cell) => cell.trim());

      return {
        dimension: cells[0] ?? '',
        lakehold: cells[1] ?? '',
        motherduck: cells[2] ?? '',
        clickhouse: cells[3] ?? '',
        cloud: cells[4] ?? '',
      };
    });
}

test.describe('@website /compare capability contract', () => {
  test('every rendered LakeHold claim has current evidence', async ({ page }) => {
    await page.goto('/compare');

    const renderedMatrix = await page.locator('.matrix tbody tr').evaluateAll((rows) =>
      rows.map((row) => {
        const cells = row.querySelectorAll('th, td');
        const lakehold = cells[1] as HTMLElement | undefined;
        return {
          dimension: cells[0]?.textContent?.trim() ?? '',
          lakehold: lakehold?.textContent?.trim() ?? '',
          motherduck: cells[2]?.textContent?.trim() ?? '',
          clickhouse: cells[3]?.textContent?.trim() ?? '',
          cloud: cells[4]?.textContent?.trim() ?? '',
          tone:
            [...(lakehold?.classList ?? [])]
              .find((name) => name.startsWith('v-'))
              ?.replace('v-', '') ?? '',
        };
      }),
    );

    expect(
      renderedMatrix.map(({ dimension, lakehold, tone }) => ({
        dimension,
        claim: lakehold,
        tone,
      })),
    ).toEqual(compareCapabilities.map(({ dimension, claim, tone }) => ({ dimension, claim, tone })));

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

  test('README keeps the complete comparison matrix in sync', async ({ page }) => {
    await page.goto('/compare');

    const renderedMatrix = await page.locator('.matrix tbody tr').evaluateAll((rows) =>
      rows.map((row) => {
        const cells = row.querySelectorAll('th, td');
        return {
          dimension: cells[0]?.textContent?.trim() ?? '',
          lakehold: cells[1]?.textContent?.trim() ?? '',
          motherduck: cells[2]?.textContent?.trim() ?? '',
          clickhouse: cells[3]?.textContent?.trim() ?? '',
          cloud: cells[4]?.textContent?.trim() ?? '',
        };
      }),
    );

    expect(renderedMatrix).toEqual(readReadmeMatrix());
  });

  test('decision guidance, limitations, objection, and workbench handoff stay usable', async ({
    page,
    request,
  }) => {
    // The handoff lands on the Workbench, which requires a credential like every other surface.
    // Without one this asserts the sign-in panel rather than the editor it is here to check.
    await signIn(page, request);
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
