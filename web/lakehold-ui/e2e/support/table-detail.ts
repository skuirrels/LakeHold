import { expect, type Page } from '@playwright/test';

/**
 * Switches the table-detail dialog to one of its sections, and proves it switched.
 *
 * The dialog renders its overview first and re-renders as physical and profile data arrive, so a
 * click issued in that window can land on a button the next render replaces. Playwright reports
 * that as a success — it clicked something — and the failure then surfaces five seconds later as
 * the section's content never appearing, which reads like slow profiling rather than a click that
 * never took. That cost an investigation once already.
 *
 * Retrying the click until the tab is marked active makes the switch deterministic and, when it
 * genuinely cannot switch, fails pointing at the tab rather than at whatever the section contains.
 */
export async function openDetailSection(
  page: Page,
  name: 'Overview' | 'Files' | 'Columns',
): Promise<void> {
  const tab = page
    .getByRole('navigation', { name: 'Table detail sections' })
    .getByRole('button', { name, exact: true });

  await expect(async () => {
    await tab.click();
    await expect(tab).toHaveClass(/active/, { timeout: 1_000 });
  }).toPass({ timeout: 15_000 });
}
