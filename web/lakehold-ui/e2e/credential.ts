import { expect, type APIRequestContext, type Page } from '@playwright/test';

/**
 * Signs a browser test in.
 *
 * The test stack requires authentication, because a stack that authenticates differently from
 * production proves nothing about production. These suites therefore have to acquire a credential
 * the same way an operator does: the disposable stack is given a known bootstrap token, and that
 * instance credential is exchanged for a tenant owner token.
 *
 * The bootstrap token is instance-scoped and deliberately cannot read tenant data, so it is not
 * usable for the workbench itself — the exchange is not ceremony, it is the only path that works.
 */
const bootstrapToken =
  process.env['LAKEHOLD_E2E_BOOTSTRAP_TOKEN'] ??
  'lkh_admin_e2ebootstrapcredential00000000000000000000';

/** The seeded workspace every browser suite works against. */
export const testTenant = 'demo';

/** Mints an owner credential for the seeded workspace. */
export async function issueOwnerToken(
  request: APIRequestContext,
  name = `e2e-${Date.now()}`,
): Promise<string> {
  const response = await request.post(`/api/tenants/${testTenant}/tokens`, {
    headers: { Authorization: `Bearer ${bootstrapToken}` },
    data: { name, role: 'owner' },
  });

  expect(
    response.ok(),
    `Could not mint an owner token with the bootstrap credential (${response.status()}). The test `
      + 'stack must set Lakehold__BootstrapToken; see compose.test.yaml.',
  ).toBe(true);

  const { token } = (await response.json()) as { token: string };
  return token;
}

/**
 * Puts a credential in the browser before any application script runs.
 *
 * `addInitScript` rather than a post-navigation write: the workbench reads storage while
 * constructing its auth service, so a token written after `goto` would arrive one load too late and
 * the first request would still be anonymous.
 */
export async function signIn(page: Page, request: APIRequestContext): Promise<void> {
  const token = await issueOwnerToken(request);
  await page.addInitScript((value) => {
    window.localStorage.setItem('lakehold.token', value);
  }, token);
}
