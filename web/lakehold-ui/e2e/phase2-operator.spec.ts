import { createHmac } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { resolve } from 'node:path';
import { expect, test, type Page } from '@playwright/test';

const enabled = process.env['LAKEHOLD_PHASE2'] === '1';
const apiUrl = process.env['LAKEHOLD_PHASE2_API_URL'] ?? 'http://127.0.0.1:6200';
const receiverUrl = process.env['LAKEHOLD_PHASE2_WEBHOOK_URL'] ?? 'http://127.0.0.1:6190';
const bootstrapToken =
  process.env['LAKEHOLD_PHASE2_BOOTSTRAP_TOKEN'] ??
  'lkh_admin_phase2bootstrapcredential0000000000000000000';
const catalog = 'analytics';
const repoRoot = resolve(process.cwd(), '../..');
const composeFiles = [
  '-f',
  resolve(repoRoot, 'compose.production.yaml'),
  '-f',
  resolve(repoRoot, 'compose.build.yaml'),
  '-f',
  resolve(repoRoot, 'compose.phase2.yaml'),
];

test.describe('disposable operator simulation', () => {
  test.skip(
    !enabled,
    'Run with npm run test:e2e:phase2; this suite destroys its disposable state.',
  );

  test('@phase2 provisions, operates, integrates, and recovers a fresh node', async ({ page }) => {
    test.setTimeout(120_000);
    let ownerToken = '';
    let editorToken = '';
    let readerToken = '';

    await test.step('reject a bad credential, then bootstrap a fresh production node', async () => {
      await page.goto('/workbench');
      await expect(
        page.getByRole('heading', { name: 'Sign in to this LakeHold node' }),
      ).toBeVisible();

      const firstRun = page.locator('lh-first-run');
      await firstRun.getByLabel('API token').fill('lkh_admin_not-a-valid-credential');
      await firstRun.getByRole('button', { name: 'Sign in' }).click();
      await expect(page.getByText('The token this tab holds was refused')).toBeVisible();

      await firstRun.getByLabel('API token').fill(bootstrapToken);
      await firstRun.getByRole('button', { name: 'Sign in' }).click();
      await expect(page.getByRole('heading', { name: 'No workspaces yet' })).toBeVisible();

      await page.getByLabel('Workspace slug').fill('phase2');
      await page.getByLabel('Display name').fill('Phase 2 operator');
      await page.getByLabel('Catalog name').fill(catalog);
      await page.getByRole('button', { name: 'Create workspace' }).click();

      await expect(page.getByRole('heading', { name: 'Workspace ready' })).toBeVisible();
      ownerToken = (await page.locator('pre.token').textContent())?.trim() ?? '';
      expect(ownerToken).toMatch(/^lkh_phase2_/);
      await page.getByRole('button', { name: 'I have saved it — open the workspace' }).click();

      await expect(page.getByLabel('SQL editor')).toBeVisible();
      await expect(page.locator('.selectors select').nth(0)).toHaveValue('phase2');
      await expect(page.locator('.selectors select').nth(1)).toHaveValue(catalog);
    });

    await test.step('create data and verify query, schema, and audit history through the UI', async () => {
      await runSql(
        page,
        "CREATE TABLE phase2_events (id BIGINT, message VARCHAR); INSERT INTO phase2_events VALUES (1, 'created')",
      );
      await runSql(page, 'SELECT id, message FROM phase2_events ORDER BY id');
      await expect(page.getByRole('cell', { name: 'created', exact: true })).toBeVisible();

      await page.getByRole('button', { name: 'History' }).click();
      await expect(
        page.locator('.history-row').filter({ hasText: 'phase2_events' }).first(),
      ).toBeVisible();
    });

    await test.step('exercise all three roles, revocation, expiry, and browser recovery, including revocation through a real psql connection', async () => {
      const editor = await apiJson<{ token: string }>('/api/tenants/phase2/tokens', ownerToken, {
        method: 'POST',
        body: { name: 'persistent-editor', role: 'editor', catalogName: catalog },
      });
      editorToken = editor.token;

      const reader = await apiJson<{ token: string }>('/api/tenants/phase2/tokens', ownerToken, {
        method: 'POST',
        body: { name: 'persistent-reader', role: 'reader', catalogName: catalog },
      });
      readerToken = reader.token;

      const listed = await apiJson<Array<{ name: string; role: string }>>(
        '/api/tenants/phase2/tokens',
        ownerToken,
      );
      expect(
        listed
          .filter((token) =>
            ['workbench', 'persistent-editor', 'persistent-reader'].includes(token.name),
          )
          .map((token) => token.role)
          .sort(),
      ).toEqual(['Editor', 'Owner', 'Reader']);

      const editorWrite = await fetch(`${apiUrl}/api/tenants/phase2/catalogs/${catalog}/query`, {
        method: 'POST',
        headers: {
          authorization: `Bearer ${editorToken}`,
          'content-type': 'application/json',
        },
        body: JSON.stringify({
          sql: 'CREATE TABLE role_probe (id INTEGER); DROP TABLE role_probe',
        }),
      });
      expect(editorWrite.status, await editorWrite.text()).toBe(200);

      const readerRead = await apiJson<{ rows: unknown[][] }>(
        `/api/tenants/phase2/catalogs/${catalog}/query`,
        readerToken,
        { method: 'POST', body: { sql: 'SELECT count(*) FROM phase2_events' } },
      );
      expect(readerRead.rows).toEqual([[1]]);

      const readerWrite = await fetch(`${apiUrl}/api/tenants/phase2/catalogs/${catalog}/query`, {
        method: 'POST',
        headers: {
          authorization: `Bearer ${readerToken}`,
          'content-type': 'application/json',
        },
        body: JSON.stringify({ sql: 'CREATE TABLE reader_must_not_write (id INTEGER)' }),
      });
      expect(readerWrite.status).toBe(400);
      expect(await readerWrite.text()).toMatch(/read.only/i);

      const editorOwnerOperation = await fetch(
        `${apiUrl}/api/tenants/phase2/catalogs/${catalog}/maintenance/cleanup`,
        {
          method: 'POST',
          headers: { authorization: `Bearer ${editorToken}` },
        },
      );
      expect(editorOwnerOperation.status).toBe(403);

      const revocable = await apiJson<{ id: number; token: string }>(
        '/api/tenants/phase2/tokens',
        ownerToken,
        {
          method: 'POST',
          body: { name: 'revocable-reader', role: 'reader', catalogName: catalog },
        },
      );

      const beforeRevoke = await fetch(`${apiUrl}/api/tenants`, {
        headers: { authorization: `Bearer ${revocable.token}` },
      });
      expect(beforeRevoke.status).toBe(200);

      expect(pgQuery(revocable.token, 'SELECT count(*) FROM phase2_events')).toBe('1');

      const revoke = await fetch(`${apiUrl}/api/tenants/phase2/tokens/${revocable.id}`, {
        method: 'DELETE',
        headers: { authorization: `Bearer ${ownerToken}` },
      });
      expect(revoke.status).toBe(204);

      const afterRevoke = await fetch(`${apiUrl}/api/tenants`, {
        headers: { authorization: `Bearer ${revocable.token}` },
      });
      expect(afterRevoke.status).toBe(401);
      expect(() => pgQuery(revocable.token, 'SELECT 1')).toThrow();

      const expired = await apiJson<{ token: string }>('/api/tenants/phase2/tokens', ownerToken, {
        method: 'POST',
        body: {
          name: 'already-expired',
          role: 'reader',
          expiresUtc: new Date(Date.now() - 60_000).toISOString(),
        },
      });
      const expiredResponse = await fetch(`${apiUrl}/api/tenants`, {
        headers: { authorization: `Bearer ${expired.token}` },
      });
      expect(expiredResponse.status).toBe(401);

      await page.getByRole('button', { name: 'Token set' }).click();
      await page.getByLabel('API token').fill(revocable.token);
      await page.getByRole('button', { name: 'Save' }).click();
      await expect(page.getByText('The token this tab holds was refused')).toBeVisible();

      await page.locator('lh-first-run').getByLabel('API token').fill(ownerToken);
      await page.locator('lh-first-run').getByRole('button', { name: 'Sign in' }).click();
      await expect(page.getByLabel('SQL editor')).toBeVisible();
    });

    await test.step('use authenticated MCP reads and operator-gated writes as external clients', async () => {
      const discovery = await mcp(ownerToken, 'server/discover', {});
      expect(discovery.result).toBeTruthy();

      const tools = await mcp(ownerToken, 'tools/list', {});
      const names = tools.result.tools.map((tool: { name: string }) => tool.name).sort();
      expect(names).toEqual([
        'describe_schema',
        'execute',
        'list_changes',
        'list_snapshots',
        'list_tenants',
        'query',
      ]);

      const query = await mcp(ownerToken, 'tools/call', {
        name: 'query',
        arguments: {
          tenant: 'phase2',
          catalog,
          sql: 'SELECT count(*) AS rows FROM phase2_events',
        },
      });
      const text = query.result.content.find(
        (block: { type: string; text?: string }) => block.type === 'text',
      )?.text;
      const payload = JSON.parse(text ?? '{}') as { rows: unknown[][] };
      expect(payload.rows).toEqual([[1]]);

      const queryCannotWrite = await mcp(ownerToken, 'tools/call', {
        name: 'query',
        arguments: {
          tenant: 'phase2',
          catalog,
          sql: 'CREATE TABLE query_must_stay_read_only (id INTEGER)',
        },
      });
      expect(queryCannotWrite.result.isError).toBe(true);
      expect(mcpText(queryCannotWrite)).toMatch(/read.only/i);

      const editorWrite = await mcp(editorToken, 'tools/call', {
        name: 'execute',
        arguments: {
          tenant: 'phase2',
          catalog,
          sql: 'CREATE TABLE mcp_probe (id INTEGER)',
        },
      });
      expect(editorWrite.result.isError).not.toBe(true);

      const readerWrite = await mcp(readerToken, 'tools/call', {
        name: 'execute',
        arguments: {
          tenant: 'phase2',
          catalog,
          sql: 'CREATE TABLE mcp_reader_must_not_write (id INTEGER)',
        },
      });
      expect(readerWrite.result.isError).toBe(true);
      expect(mcpText(readerWrite)).toMatch(/read.only/i);
    });

    let restoreSnapshot = 0;
    await test.step('read the typed feed and verify signed webhook failure, retry, and cursor advancement', async () => {
      const snapshots = await apiJson<Array<{ snapshotId: number }>>(
        `/api/tenants/phase2/catalogs/${catalog}/snapshots?limit=25`,
        ownerToken,
      );
      restoreSnapshot = snapshots[0].snapshotId;

      await fetch(`${receiverUrl}/reset`, { method: 'POST' });
      await fetch(`${receiverUrl}/fail-next`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ count: 1 }),
      });

      await page.getByRole('button', { name: 'Changes' }).click();
      await page.getByRole('button', { name: 'New subscription' }).click();
      await page.getByLabel('Endpoint URL').fill('http://webhook:9080/hook');
      await page.getByLabel('Signing secret').fill('phase2-webhook-signing-secret');
      await page.getByLabel('Table').last().selectOption('main.phase2_events');
      await page.getByRole('button', { name: 'Create', exact: true }).click();
      await expect(page.getByText('http://webhook:9080/hook')).toBeVisible();

      await runSql(page, "INSERT INTO phase2_events VALUES (2, 'delivered')");

      const state = await expect
        .poll(
          async () => {
            const response = await fetch(`${receiverUrl}/state`);
            return response.json() as Promise<ReceiverState>;
          },
          { timeout: 15_000 },
        )
        .toMatchObject({ failuresRemaining: 0, deliveries: [{ status: 503 }, { status: 204 }] });

      const receiverState = (await (await fetch(`${receiverUrl}/state`)).json()) as ReceiverState;
      expect(receiverState.deliveries).toHaveLength(2);
      for (const delivery of receiverState.deliveries) {
        const signature =
          'sha256=' +
          createHmac('sha256', 'phase2-webhook-signing-secret').update(delivery.body).digest('hex');
        expect(delivery.signature).toBe(signature);
        expect(delivery.delivery).toMatch(/^[a-f0-9]{32}$/);
      }
      expect(receiverState.deliveries[1].delivery).toBe(receiverState.deliveries[0].delivery);
      expect(receiverState.deliveries[1].body).toBe(receiverState.deliveries[0].body);
      expect(receiverState.deliveries[1].signature).toBe(receiverState.deliveries[0].signature);

      await page.getByRole('button', { name: 'Changes' }).click();
      await expect(page.getByText('delivering', { exact: true })).toBeVisible();
      await expect(page.locator('.subs tbody tr').first()).toContainText('snapshot');
      await page.getByRole('button', { name: 'Read changes' }).click();
      await expect(page.locator('lh-changes-panel .panel-summary')).toContainText(/change/);
      await expect(page.locator('lh-changes-panel')).toContainText('delivered');
      void state;
    });

    await test.step('query an earlier snapshot, restore it, and verify the live data', async () => {
      await runSql(
        page,
        `SELECT count(*) AS rows_at_snapshot FROM phase2_events AT (VERSION => ${restoreSnapshot})`,
      );
      await expect(page.locator('lh-result-grid thead')).toContainText('rows_at_snapshot');
      await expect(page.locator('lh-result-grid tbody tr').first().locator('td').nth(1)).toHaveText(
        '1',
      );

      await page.getByRole('button', { name: 'Snapshots' }).click();
      const snapshotRow = page
        .locator('table.snapshots tbody tr')
        .filter({ has: page.getByRole('cell', { name: String(restoreSnapshot), exact: true }) });
      await snapshotRow.getByRole('button', { name: 'Restore…' }).click();
      await expect(page.getByLabel('SQL editor')).toHaveValue(
        new RegExp(
          `CREATE OR REPLACE TABLE[\\s\\S]+AT \\(VERSION => ${restoreSnapshot}\\)`,
        ),
      );
      const generatedRestore = await page.getByLabel('SQL editor').inputValue();
      const targetedRestore = generatedRestore
        .replace(/^CREATE OR REPLACE TABLE \S+/m, 'CREATE OR REPLACE TABLE main.phase2_events')
        .replace(/^SELECT \* FROM \S+ AT/m, 'SELECT * FROM main.phase2_events AT');
      await runSql(page, targetedRestore);

      await runSql(page, 'SELECT count(*) AS rows_after_restore FROM phase2_events');
      await expect(page.locator('lh-result-grid thead')).toContainText('rows_after_restore');
      await expect(page.locator('lh-result-grid tbody tr').first().locator('td').nth(1)).toHaveText(
        '1',
      );
    });

    await test.step('backup, restore to a new catalog file, and refuse overwrite', async () => {
      await page.getByRole('button', { name: 'Backup', exact: true }).click();
      await expect(page.locator('.ok-banner')).toContainText(/backup/i);
      await page.getByRole('button', { name: 'Backups' }).click();
      await page.getByRole('button', { name: 'Restore…' }).first().click();
      await page.getByLabel('Target metadata path').fill('phase2-restored.ducklake');
      await page.getByRole('button', { name: 'Restore', exact: true }).click();
      await expect(page.locator('lh-backups-panel .ok-banner')).toContainText(
        /phase2-restored\.ducklake/,
      );

      await page.getByRole('button', { name: 'Restore…' }).first().click();
      await page.getByLabel('Target metadata path').fill('phase2-restored.ducklake');
      await page.getByRole('button', { name: 'Restore', exact: true }).click();
      await expect(page.locator('lh-backups-panel')).toContainText(
        /already exists|never overwrites/i,
      );
    });

    await test.step('apply destructive maintenance only on disposable state and verify signed export', async () => {
      for (const operation of ['Expire', 'Cleanup']) {
        await page.getByRole('button', { name: operation }).click();
        await expect(page.getByText('Dry run — nothing was changed.')).toBeVisible();
        await page.getByRole('button', { name: 'Apply for real' }).click();
        await expect(page.getByRole('button', { name: 'Apply for real' })).toBeHidden();
        await expect(page.locator('.ok-banner')).toContainText(new RegExp(operation, 'i'));
      }

      await page.getByRole('button', { name: 'Eject' }).click();
      await page.getByRole('button', { name: 'Eject now' }).click();
      await expect(page.locator('lh-eject-panel .ok-banner')).toContainText('Verified. Signed.', {
        timeout: 30_000,
      });
    });

    await test.step('independently verify the final API and wire-protocol state', async () => {
      const result = await apiJson<{ rows: unknown[][] }>(
        `/api/tenants/phase2/catalogs/${catalog}/query`,
        ownerToken,
        { method: 'POST', body: { sql: 'SELECT count(*) FROM phase2_events' } },
      );
      expect(result.rows).toEqual([[1]]);
      expect(pgQuery(ownerToken, 'SELECT count(*) FROM phase2_events')).toBe('1');

      const backups = await apiJson<Array<{ complete: boolean }>>(
        `/api/tenants/phase2/catalogs/${catalog}/backups`,
        ownerToken,
      );
      expect(backups.some((backup) => backup.complete)).toBe(true);

      const ejects = await apiJson<
        Array<{ complete: boolean; isSigned: boolean; tables: unknown[] }>
      >(`/api/tenants/phase2/catalogs/${catalog}/ejects`, ownerToken);
      expect(
        ejects.some((eject) => eject.complete && eject.isSigned && eject.tables.length > 0),
      ).toBe(true);
    });
  });
});

async function runSql(page: Page, sql: string): Promise<void> {
  await page.getByLabel('SQL editor').fill(sql);
  const responsePromise = page.waitForResponse(
    (response) =>
      response.url().includes(`/api/tenants/phase2/catalogs/${catalog}/query`) &&
      response.request().method() === 'POST',
  );
  await page.getByRole('button', { name: /^Run/ }).click();
  const response = await responsePromise;
  expect(response.ok(), await response.text()).toBe(true);
  await expect(page.getByRole('button', { name: /^Run/ })).toBeEnabled();
}

async function apiJson<T>(
  path: string,
  token: string,
  options: { method?: string; body?: unknown } = {},
): Promise<T> {
  const response = await fetch(`${apiUrl}${path}`, {
    method: options.method ?? 'GET',
    headers: {
      authorization: `Bearer ${token}`,
      ...(options.body === undefined ? {} : { 'content-type': 'application/json' }),
    },
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
  });
  const body = await response.text();
  expect(response.ok, body).toBe(true);
  return JSON.parse(body) as T;
}

function pgQuery(token: string, sql: string): string {
  return execFileSync(
    'docker',
    [
      'compose',
      '-p',
      'lakehold-phase2',
      ...composeFiles,
      'run',
      '--rm',
      '--no-deps',
      '-e',
      `PGPASSWORD=${token}`,
      'pg-client',
      '-h',
      'api',
      '-p',
      '5433',
      '-U',
      'phase2',
      '-d',
      catalog,
      '-Atc',
      sql,
    ],
    { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] },
  ).trim();
}

async function mcp(token: string, method: string, params: Record<string, unknown>) {
  const meta = {
    'io.modelcontextprotocol/protocolVersion': '2026-07-28',
    'io.modelcontextprotocol/clientInfo': { name: 'lakehold-phase2', version: '1.0' },
    'io.modelcontextprotocol/clientCapabilities': {},
    ...((params['_meta'] as Record<string, unknown> | undefined) ?? {}),
  };
  const headers: Record<string, string> = {
    accept: 'application/json, text/event-stream',
    authorization: `Bearer ${token}`,
    'content-type': 'application/json',
    'mcp-method': method,
    'mcp-protocol-version': '2026-07-28',
  };
  if (typeof params['name'] === 'string') {
    headers['mcp-name'] = params['name'];
  }

  const response = await fetch(`${apiUrl}/mcp`, {
    method: 'POST',
    headers,
    body: JSON.stringify({
      jsonrpc: '2.0',
      id: crypto.randomUUID(),
      method,
      params: { ...params, _meta: meta },
    }),
  });
  const body = await response.text();
  expect(response.ok, body).toBe(true);
  const dataLine = body.split('\n').find((line) => line.startsWith('data:'));
  const json = dataLine?.slice(5).trim() ?? body;
  return JSON.parse(json ?? '{}');
}

function mcpText(response: {
  result?: { content?: Array<{ type: string; text?: string }> };
}): string {
  return (
    response.result?.content
      ?.filter((block) => block.type === 'text')
      .map((block) => block.text ?? '')
      .join(' ') ?? ''
  );
}

interface ReceiverState {
  failuresRemaining: number;
  deliveries: Array<{
    body: string;
    delivery: string | null;
    signature: string | null;
    status: number;
  }>;
}
