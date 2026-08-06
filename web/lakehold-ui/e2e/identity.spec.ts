import { expect, test, type Page } from '@playwright/test';

/**
 * Browser sign-in through the bundled identity provider.
 *
 * These exist because the sign-in path was, for a long time, the one surface nobody exercised: it
 * was optional, off by default, and consequently broken in ways nobody noticed. Each test asserts
 * something an administrator would otherwise discover by being confused — that signing in works at
 * all, that the two seeded users get *different* capabilities, and that the difference is visible
 * on screen rather than implied.
 *
 * Playwright gives every test a fresh browser context, which is what makes signing in as two
 * different people straightforward: there is no session to tear down between them.
 */
const skip = process.env['LAKEHOLD_SKIP_IDENTITY_E2E'] === '1';

/** Signs in through the identity provider and lands back on the Workbench. */
async function signInWith(page: Page, username: string): Promise<void> {
  await page.goto('/auth/login?returnUrl=/workbench');

  // The provider's own login page, not LakeHold's. Reaching it at all proves the redirect, the
  // client registration, and the realm import are wired correctly.
  await expect(page.locator('#kc-form-login')).toBeVisible();
  await page.locator('#username').fill(username);
  await page.locator('#password').fill('lakehold');
  await page.locator('#kc-form-login').getByRole('button', { name: /sign in/i }).click();

  await page.waitForURL(/\/workbench/);
}

/** Opens the People destination, where workspace membership and client tokens are administered. */
async function openPeople(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'People' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'People' })).toBeVisible();
}

/** Reads the session the API reports for this browser. */
async function session(page: Page): Promise<{
  oidcEnabled: boolean;
  authenticated: boolean;
  displayName: string | null;
  systemAdmin: boolean;
}> {
  const response = await page.request.get('/auth/session');
  expect(response.ok()).toBe(true);
  return response.json();
}

test.describe('identity provider sign-in', () => {
  test.skip(skip, 'requires the bundled Keycloak service');

  test('offers a provider sign-in rather than only a token box', async ({ page }) => {
    await page.goto('/workbench');

    // The affordance an operator looks for first. When no provider is configured this is absent and
    // the token box is all there is, which is the state that made people ask where the login was.
    await expect(page.getByRole('link', { name: /continue with your identity provider/i })).toBeVisible();
  });

  test('signs a workspace owner in and gives them the workspace', async ({ page }) => {
    await signInWith(page, 'analyst');

    await expect(page.getByLabel('SQL editor')).toBeVisible();
    await expect(page.getByText('Owen Owner')).toBeVisible();
    await expect(page.getByRole('link', { name: 'Sign out' })).toBeVisible();

    const state = await session(page);
    expect(state.authenticated).toBe(true);
    expect(state.displayName).toBe('Owen Owner');
    // Reaching data is a tenant capability. An owner of one workspace is not an instance
    // administrator, and conflating the two is the confusion these two users exist to dispel.
    expect(state.systemAdmin).toBe(false);
  });

  test('signs an instance administrator in and gives them administration instead', async ({
    page,
  }) => {
    await signInWith(page, 'admin');

    const state = await session(page);
    expect(state.authenticated).toBe(true);
    expect(state.displayName).toBe('Ada Administrator');
    expect(state.systemAdmin).toBe(true);

    // The administrator lands on administration, not on a SQL editor they cannot use: an instance
    // credential provisions tenants and credentials and deliberately cannot read tenant data.
    await expect(page.getByRole('heading', { name: 'System Settings' })).toBeVisible();
    await expect(page.getByText('Ada Administrator')).toBeVisible();
  });

  test('lists a person who has signed in, and lets an administrator change what they reach', async ({
    browser,
    page,
  }) => {
    // A separate context so this is a genuinely different person, not the same session twice.
    const theirs = await browser.newContext();
    try {
      const them = await theirs.newPage();
      await signInWith(them, 'analyst');
      await expect(them.getByLabel('SQL editor')).toBeVisible();
    } finally {
      await theirs.close();
    }

    await signInWith(page, 'admin');
    await openPeople(page);

    // The point of the whole membership model: somebody who signed in is now a record an
    // administrator can see and act on, rather than an invisible consequence of a claim.
    const people = page.locator('lh-member-administration');
    await expect(people.getByRole('heading', { name: 'Everyone who has signed in' })).toBeVisible();
    await expect(people.getByText('Owen Owner')).toBeVisible();

    const role = people.getByLabel('Role for Owen Owner');

    // Deliberately not asserting the starting role: this test changes server state, so depending on
    // what it was would make a retry fail against the value the first attempt already wrote.
    await role.selectOption('reader');
    await expect(role).toHaveValue('reader');

    // The claim still says owner on every login. Reloading proves LakeHold's decision is the one
    // that holds -- which is the entire reason membership exists rather than trusting the claim.
    // An instance credential lands on System Settings, so the page has to be reopened first.
    await page.reload();
    await openPeople(page);
    await expect(people.getByLabel('Role for Owen Owner')).toHaveValue('reader');

    await expect(people.getByRole('button', { name: 'Suspend' })).toBeVisible();
    await expect(people.getByRole('button', { name: 'Remove' })).toBeVisible();

    // Leave the workspace as it was found, so this suite can run twice in a row.
    await people.getByLabel('Role for Owen Owner').selectOption('owner');
    await expect(people.getByLabel('Role for Owen Owner')).toHaveValue('owner');
  });

  test('shows an administrator how to issue credentials for clients and agents', async ({
    page,
  }) => {
    await signInWith(page, 'admin');
    await openPeople(page);

    // An administrator arriving for the first time has to be able to find this without being told.
    // If the panel or its controls disappear, that is a confused administrator, so it fails here.
    await expect(page.getByRole('heading', { name: 'API tokens' })).toBeVisible();
    await expect(page.getByLabel('Token name')).toBeVisible();
    // Exactly the role picker, not the label that also appears in the issued-credentials list.
    await expect(page.locator('select[name="role"]')).toBeVisible();
    await expect(page.getByRole('button', { name: /generate token/i })).toBeVisible();
  });
});
