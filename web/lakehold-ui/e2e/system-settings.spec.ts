import { expect, test, type Page } from '@playwright/test';
import { signInAsInstance } from './credential';

/**
 * Instance-level MCP controls, which are the operator's only in-product switch over an
 * agent-reachable surface.
 *
 * The component spec covers form state against a fake service. What it cannot cover is the claim
 * the feature actually makes: that a change is persisted in PostgreSQL and applied to a live API
 * without a restart. Only a real stack can tell those apart, and getting it wrong means an operator
 * believes they have closed a surface that is still open.
 */
async function openSettings(page: Page) {
  await page.goto('/workbench');
  await page.getByRole('button', { name: 'System Settings' }).click();

  const panel = page.locator('lh-system-settings');
  await expect(panel).toBeVisible();
  await expect(panel).toContainText('Model Context Protocol');
  return panel;
}

test.describe('instance MCP settings', () => {
  test.beforeEach(async ({ page }) => {
    await signInAsInstance(page);
  });

  // Restores the surface however the test ended, including a failure part-way through. Leaving MCP
  // disabled would silently disarm the agent-authentication coverage in the rest of the suite.
  test.afterEach(async ({ page }) => {
    const panel = await openSettings(page);
    const enable = panel.getByLabel('Enable MCP server');
    const writes = panel.getByLabel('Allow write commands');
    if (!(await enable.isChecked())) {
      await enable.check();
    }

    if (await writes.isChecked()) {
      await writes.uncheck();
    }

    await panel.getByRole('button', { name: /Save settings/ }).click();
    await expect(panel).not.toContainText('Saving…');
  });

  test('persists a live control across a reload', async ({ page }) => {
    const panel = await openSettings(page);
    const writes = panel.getByLabel('Allow write commands');
    await expect(writes).not.toBeChecked();

    await writes.check();
    // The save has to land before the reload; navigating mid-flight aborts the request and the test
    // then measures nothing but a race it created itself.
    const saved = page.waitForResponse(
      (response) => response.request().method() !== 'GET' && response.url().includes('settings'),
    );
    await panel.getByRole('button', { name: /Save settings/ }).click();
    expect((await saved).ok()).toBe(true);

    // A reload proves the control is durable rather than a signal that survives only until the
    // component is destroyed — the whole point of persisting it in PostgreSQL.
    await page.reload();
    const reopened = await openSettings(page);
    await expect(reopened.getByLabel('Allow write commands')).toBeChecked();
  });

  test('applies a disabled MCP server to the live endpoint without a restart', async ({
    page,
    request,
  }) => {
    const panel = await openSettings(page);

    // Enabled and reachable first, so the refusal below cannot be mistaken for a route that was
    // never mapped. A credential-less call is a 401 while the surface is on.
    await expect(panel.getByLabel('Enable MCP server')).toBeChecked();
    const before = await request.post('/mcp', {
      headers: { 'Content-Type': 'application/json' },
      data: { jsonrpc: '2.0', id: 1, method: 'tools/list' },
      failOnStatusCode: false,
    });
    expect(before.status()).toBe(401);

    await panel.getByLabel('Enable MCP server').uncheck();
    await panel.getByRole('button', { name: /Save settings/ }).click();
    await expect(panel).not.toContainText('Saving…');

    // Disabled means undiscoverable, not merely refused: the endpoint answers 404 so a disabled
    // agent surface cannot be distinguished from one that was never deployed. No restart happened
    // between these two calls, which is the behaviour under test.
    await expect
      .poll(
        async () => {
          const response = await request.post('/mcp', {
            headers: { 'Content-Type': 'application/json' },
            data: { jsonrpc: '2.0', id: 1, method: 'tools/list' },
            failOnStatusCode: false,
          });
          return response.status();
        },
        { timeout: 20_000 },
      )
      .toBe(404);
  });
});
