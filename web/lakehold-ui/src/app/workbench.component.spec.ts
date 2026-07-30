import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { of, Subject } from 'rxjs';
import { ApiError, LakehouseService } from './lakehouse.service';
import { FakeLakehouseService, tableStorage } from './test-doubles';
import { WorkbenchComponent } from './workbench.component';

describe('WorkbenchComponent', () => {
  let api: FakeLakehouseService;
  let fixture: ComponentFixture<WorkbenchComponent>;
  const originalMatchMedia = window.matchMedia;

  async function mount() {
    fixture = TestBed.createComponent(WorkbenchComponent);
    await fixture.whenStable();
    return fixture;
  }

  function text(): string {
    return fixture.nativeElement.textContent ?? '';
  }

  function useCompactViewport(compact: boolean): void {
    Object.defineProperty(window, 'matchMedia', {
      configurable: true,
      value: () =>
        ({
          matches: compact,
          media: '(max-width: 900px)',
          onchange: null,
          addEventListener: () => undefined,
          removeEventListener: () => undefined,
          addListener: () => undefined,
          removeListener: () => undefined,
          dispatchEvent: () => true,
        }) satisfies MediaQueryList,
    });
  }

  /** Clicks a bottom-panel tab by its label. */
  async function openTab(label: string): Promise<void> {
    const tab = [...fixture.nativeElement.querySelectorAll('.tabs .tab')].find(
      (b) => (b as HTMLElement).textContent?.trim() === label,
    ) as HTMLButtonElement;
    tab.click();
    await fixture.whenStable();
  }

  /** Clicks a maintenance button by its label. */
  async function maintain(label: string): Promise<void> {
    const button = [...fixture.nativeElement.querySelectorAll('.maintenance .btn')].find(
      (b) => (b as HTMLElement).textContent?.trim() === label,
    ) as HTMLButtonElement;
    button.click();
    await fixture.whenStable();
  }

  beforeEach(() => {
    sessionStorage.clear();
    api = new FakeLakehouseService();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: LakehouseService, useValue: api },
      ],
    });
  });

  it('lands an instance credential on System Settings instead of the tenant SQL editor', async () => {
    api.access = {
      mode: 'authenticated',
      role: 'owner',
      readOnly: false,
      systemAdmin: true,
    };

    await mount();

    expect(text()).toContain('System Settings');
    expect(fixture.nativeElement.querySelector('lh-system-settings')).toBeTruthy();
    expect(api.countOf('getSchemas')).toBe(0);
  });

  it('uses an authenticated browser session and offers logout without retaining a pasted token', async () => {
    sessionStorage.setItem('lakehold.token', 'lkh_stale_machine_token');
    api.browserSession = {
      oidcEnabled: true,
      authenticated: true,
      displayName: 'Ada Administrator',
      systemAdmin: true,
    };
    api.access = {
      mode: 'authenticated',
      role: 'owner',
      readOnly: false,
      systemAdmin: true,
    };

    await mount();

    expect(sessionStorage.getItem('lakehold.token')).toBeNull();
    expect(text()).toContain('Ada Administrator');
    expect(
      (fixture.nativeElement.querySelector('.browser-session a') as HTMLAnchorElement).getAttribute(
        'href',
      ),
    ).toBe('/auth/logout?returnUrl=/workbench');
    expect(fixture.nativeElement.querySelector('lh-system-settings')).toBeTruthy();
  });

  it('offers first-run provisioning to an instance administrator on an empty node', async () => {
    api.access = {
      mode: 'authenticated',
      role: 'owner',
      readOnly: false,
      systemAdmin: true,
    };
    api.tenants = [];

    await mount();

    expect(fixture.nativeElement.querySelector('lh-system-settings')).toBeNull();
    expect(text()).toContain('No workspaces yet');
  });

  it('returns to the workbench when an instance credential is replaced by a tenant credential', async () => {
    api.access = {
      mode: 'authenticated',
      role: 'owner',
      readOnly: false,
      systemAdmin: true,
    };
    await mount();
    expect(fixture.nativeElement.querySelector('lh-system-settings')).toBeTruthy();

    api.access = {
      mode: 'authenticated',
      role: 'owner',
      readOnly: false,
      systemAdmin: false,
    };
    (fixture.nativeElement.querySelector('.credential > .btn') as HTMLButtonElement).click();
    await fixture.whenStable();
    const token = fixture.nativeElement.querySelector(
      '.credential input[type="password"]',
    ) as HTMLInputElement;
    token.value = 'lkh_tenant_replacement';
    token.dispatchEvent(new Event('input'));
    (
      fixture.nativeElement.querySelector('.credential-actions .btn-primary') as HTMLButtonElement
    ).click();
    await fixture.whenStable();

    expect(fixture.nativeElement.querySelector('lh-system-settings')).toBeNull();
    expect(fixture.nativeElement.querySelector('[aria-label="SQL editor"]')).toBeTruthy();
    expect(api.lastArgs('getSchemas')).toEqual(['demo', 'analytics']);
  });

  afterEach(() => {
    Object.defineProperty(window, 'matchMedia', {
      configurable: true,
      value: originalMatchMedia,
    });
  });

  it('selects the first workspace and catalog it is given', async () => {
    await mount();

    expect(api.lastArgs('getSchemas')).toEqual(['demo', 'analytics']);
    expect(text()).toContain('Demo workspace');
    expect(fixture.nativeElement.querySelector('.connection-status')?.textContent?.trim()).toBe(
      'Connected',
    );
  });

  describe('navigation shell', () => {
    it('collapses and restores the product menu without destroying explorer state', async () => {
      await mount();

      const filter = fixture.nativeElement.querySelector(
        '[aria-label="Filter catalog objects"]',
      ) as HTMLInputElement;
      filter.value = 'events';
      filter.dispatchEvent(new Event('input'));
      await fixture.whenStable();

      const toggle = fixture.nativeElement.querySelector('.nav-toggle') as HTMLButtonElement;
      const navigation = fixture.nativeElement.querySelector(
        '#workbench-navigation',
      ) as HTMLElement;

      expect(toggle.getAttribute('aria-expanded')).toBe('true');
      expect(toggle.getAttribute('aria-label')).toBe('Collapse navigation');

      toggle.click();
      await fixture.whenStable();

      expect(toggle.getAttribute('aria-expanded')).toBe('false');
      expect(toggle.getAttribute('aria-label')).toBe('Expand navigation');
      expect(navigation.getAttribute('aria-hidden')).toBe('false');
      expect(navigation.hasAttribute('inert')).toBe(false);
      expect(navigation.classList.contains('closed')).toBe(true);

      const queryHistory = navigation.querySelector(
        '[aria-label="Query history"]',
      ) as HTMLButtonElement;
      queryHistory.click();
      await fixture.whenStable();

      expect(fixture.nativeElement.querySelector('.tabs .tab.active')?.textContent?.trim()).toBe(
        'Query history',
      );

      toggle.click();
      await fixture.whenStable();

      expect(filter.value).toBe('events');
      expect(navigation.getAttribute('aria-hidden')).toBe('false');
    });

    it('uses product-navigation items as shortcuts to existing panels', async () => {
      await mount();

      const queryHistory = [...fixture.nativeElement.querySelectorAll('.nav-item')].find(
        (button) => (button as HTMLElement).textContent?.trim() === 'Query history',
      ) as HTMLButtonElement;
      queryHistory.click();
      await fixture.whenStable();

      expect(queryHistory.classList.contains('active')).toBe(true);
      expect(fixture.nativeElement.querySelector('.tabs .tab.active')?.textContent?.trim()).toBe(
        'Query history',
      );
    });

    it('starts compact navigation closed and dismisses its drawer with Escape', async () => {
      useCompactViewport(true);
      await mount();

      const root = fixture.nativeElement.querySelector('.body') as HTMLElement;
      const toggle = fixture.nativeElement.querySelector('.nav-toggle') as HTMLButtonElement;
      const navigation = fixture.nativeElement.querySelector(
        '#workbench-navigation',
      ) as HTMLElement;

      expect(toggle.getAttribute('aria-expanded')).toBe('false');
      expect(navigation.getAttribute('aria-hidden')).toBe('true');

      toggle.click();
      await fixture.whenStable();
      expect(toggle.getAttribute('aria-expanded')).toBe('true');

      root.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      await fixture.whenStable();

      expect(toggle.getAttribute('aria-expanded')).toBe('false');
      expect(navigation.getAttribute('aria-hidden')).toBe('true');
    });
  });

  describe('first run', () => {
    it('shows sign-in instead of an error when the deployment requires a credential', async () => {
      api.errors.set('getAccess', new ApiError('Unauthorized', 401));

      await mount();

      expect(text()).toContain('Sign in to this LakeHold node');
      expect(text()).not.toContain('docker compose');
      expect(text()).not.toContain('Could not load workspaces');
      expect(fixture.nativeElement.querySelector('.tabs')).toBeNull();
    });

    it('opens a friendly read-only demo without exposing mutation controls', async () => {
      api.access = { mode: 'demo', role: 'reader', readOnly: true, systemAdmin: false };
      api.backups = [
        {
          generation: '20260726T120000Z',
          createdUtc: '2026-07-26T12:00:00Z',
          snapshotId: 1,
          tableCount: 2,
          complete: true,
        },
      ];

      await mount();

      expect(text()).toContain('You’re exploring a live LakeHold demo');
      expect(text()).toContain('Operator sign in');
      expect(fixture.nativeElement.querySelector('.maintenance')).toBeNull();
      expect(fixture.nativeElement.querySelector('[aria-label="SQL editor"]')).not.toBeNull();

      await openTab('Changes');
      expect(text()).not.toContain('New subscription');

      await openTab('Backups');
      expect(text()).not.toContain('Restore…');

      await openTab('Eject');
      expect(text()).not.toContain('Eject now');
    });

    it('offers setup when the credential works but the node has no workspaces', async () => {
      api.tenants = [];

      await mount();

      expect(text()).toContain('No workspaces yet');
      expect(fixture.nativeElement.querySelector('.tabs')).toBeNull();
    });

    it('creates tenant, catalog, and owner token in order and shows the token once', async () => {
      api.tenants = [];
      await mount();

      const setInput = (input: HTMLInputElement, value: string): void => {
        input.value = value;
        input.dispatchEvent(new Event('input'));
      };

      const [slug, displayName, catalog] = [
        ...fixture.nativeElement.querySelectorAll('.first-run input[type="text"]'),
      ] as HTMLInputElement[];
      setInput(slug, 'northwind');
      setInput(displayName, 'Northwind Traders');
      setInput(catalog, 'warehouse');
      await fixture.whenStable();

      const create = [...fixture.nativeElement.querySelectorAll('button')].find(
        (button) => (button as HTMLElement).textContent?.trim() === 'Create workspace',
      ) as HTMLButtonElement;
      create.click();
      await fixture.whenStable();

      const provisioning = api.calls
        .filter((call) => ['createTenant', 'createCatalog', 'createToken'].includes(call.method))
        .map((call) => [call.method, call.args]);

      expect(provisioning).toEqual([
        ['createTenant', ['northwind', 'Northwind Traders']],
        ['createCatalog', ['northwind', 'warehouse']],
        [
          'createToken',
          [
            'northwind',
            {
              name: 'workbench',
              role: 'owner',
              readOnly: false,
              catalogName: null,
              expiresUtc: null,
            },
          ],
        ],
      ]);
      expect(text()).toContain('Workspace ready');
      expect(text()).toContain('lkh_new-owner-token');
    });
  });

  describe('the table pickers', () => {
    it('offer base tables and leave views out', async () => {
      // Neither the change feed nor a snapshot restore means anything for a view: it has no rows of
      // its own.
      api.schemas = [
        {
          name: 'main',
          tables: [
            { name: 'orders', kind: 'BASE TABLE', columns: [] },
            { name: 'revenue_by_country', kind: 'VIEW', columns: [] },
          ],
        },
      ];

      await mount();
      await openTab('Changes');

      const options = [...fixture.nativeElement.querySelectorAll('.panel-controls option')].map(
        (o) => (o as HTMLElement).textContent?.trim(),
      );
      expect(options).toContain('main.orders');
      expect(options).not.toContain('main.revenue_by_country');
    });

    it('opens table detail from the catalog explorer', async () => {
      api.schemas = [
        {
          name: 'main',
          tables: [{ name: 'orders', kind: 'BASE TABLE', columns: [] }],
        },
      ];
      api.storage = { ...api.storage, tables: [tableStorage({ tableName: 'orders' })] };
      api.detail = { ...api.detail, tableName: 'orders', storage: api.storage.tables[0] };

      await mount();
      (fixture.nativeElement.querySelector('button.inspect') as HTMLButtonElement).click();
      await fixture.whenStable();

      expect(fixture.nativeElement.querySelector('.tabs .tab.active')?.textContent?.trim()).toBe(
        'Storage',
      );
      expect(api.lastArgs('getTableDetail')).toEqual(['demo', 'analytics', 'main', 'orders']);
    });
  });

  describe('errors', () => {
    it('ignores a stale workspace response after the credential changes', async () => {
      const first = new Subject<typeof api.tenants>();
      const second = new Subject<typeof api.tenants>();
      const currentTenants: typeof api.tenants = [
        {
          slug: 'current',
          displayName: 'Current workspace',
          catalogs: [{ name: 'current_catalog', dataPath: '/current', isReadOnly: false }],
        },
      ];
      const staleTenants: typeof api.tenants = [
        {
          slug: 'stale',
          displayName: 'Stale workspace',
          catalogs: [{ name: 'stale_catalog', dataPath: '/stale', isReadOnly: false }],
        },
      ];
      let request = 0;
      api.listTenants = (...args: unknown[]) => {
        api.calls.push({ method: 'listTenants', args });
        return [first, second][request++] ?? of(api.tenants);
      };

      await mount();

      (fixture.nativeElement.querySelector('.credential > .btn') as HTMLButtonElement).click();
      await fixture.whenStable();
      const token = fixture.nativeElement.querySelector(
        '.credential input[type="password"]',
      ) as HTMLInputElement;
      token.value = 'lkh_current';
      token.dispatchEvent(new Event('input'));
      await fixture.whenStable();
      (
        fixture.nativeElement.querySelector('.credential-actions .btn-primary') as HTMLButtonElement
      ).click();
      await fixture.whenStable();

      second.next(currentTenants);
      second.complete();
      await fixture.whenStable();
      expect(text()).toContain('Current workspace');
      expect(api.lastArgs('getSchemas')).toEqual(['current', 'current_catalog']);

      first.next(staleTenants);
      first.complete();
      await fixture.whenStable();
      expect(text()).toContain('Current workspace');
      expect(text()).not.toContain('Stale workspace');
      expect(api.lastArgs('getSchemas')).toEqual(['current', 'current_catalog']);
    });

    it('names the operation rather than always blaming a query', async () => {
      api.failures.set('getSchemas', 'catalog is gone');
      await mount();

      expect(text()).toContain('Could not load the catalog');
      expect(text()).toContain('catalog is gone');
      const status = fixture.nativeElement.querySelector('.connection-status') as HTMLElement;
      expect(status.textContent?.trim()).toBe('Not connected');
      expect(status.classList.contains('disconnected')).toBe(true);
    });

    it('keeps catalog status and errors aligned across stale responses and retries', async () => {
      const first = new Subject<typeof api.schemas>();
      const second = new Subject<typeof api.schemas>();
      let request = 0;
      api.getSchemas = (...args: unknown[]) => {
        api.calls.push({ method: 'getSchemas', args });
        return [first, second][request++] ?? of(api.schemas);
      };

      await mount();
      const catalogSelect = fixture.nativeElement.querySelectorAll(
        '.selectors select',
      )[1] as HTMLSelectElement;

      catalogSelect.dispatchEvent(new Event('change'));
      await fixture.whenStable();

      second.error(new Error('new request failed'));
      await fixture.whenStable();
      expect(text()).toContain('new request failed');
      expect(text()).toContain('Not connected');

      first.next([]);
      first.complete();
      await fixture.whenStable();
      expect(text()).toContain('new request failed');
      expect(text()).toContain('Not connected');

      catalogSelect.dispatchEvent(new Event('change'));
      await fixture.whenStable();
      expect(text()).not.toContain('new request failed');
      expect(text()).toContain('Connected');
    });

    it('ignores a stale catalog failure after the current request succeeds', async () => {
      const first = new Subject<typeof api.schemas>();
      const second = new Subject<typeof api.schemas>();
      let request = 0;
      api.getSchemas = (...args: unknown[]) => {
        api.calls.push({ method: 'getSchemas', args });
        return [first, second][request++] ?? of(api.schemas);
      };

      await mount();
      const catalogSelect = fixture.nativeElement.querySelectorAll(
        '.selectors select',
      )[1] as HTMLSelectElement;

      catalogSelect.dispatchEvent(new Event('change'));
      await fixture.whenStable();

      second.next([]);
      second.complete();
      await fixture.whenStable();
      expect(fixture.nativeElement.querySelector('.connection-status')?.textContent?.trim()).toBe(
        'Connected',
      );

      first.error(new Error('stale request failed'));
      await fixture.whenStable();
      expect(text()).not.toContain('stale request failed');
      expect(fixture.nativeElement.querySelector('.connection-status')?.textContent?.trim()).toBe(
        'Connected',
      );
    });

    it('clears a query failure when the operator opens another panel', async () => {
      // A query error hanging over the eject list implies the eject failed, which is the opposite of
      // what happened. Panels carry their own banners; this one belongs to the editor.
      api.failures.set('execute', 'syntax error at or near "SELCT"');
      await mount();

      (
        fixture.nativeElement.querySelector('.editor-toolbar .btn-primary') as HTMLButtonElement
      ).click();
      await fixture.whenStable();
      expect(text()).toContain('syntax error');

      await openTab('Schedule');
      expect(text()).not.toContain('syntax error');
    });
  });

  describe('maintenance', () => {
    it('runs as a dry run first and waits for confirmation', async () => {
      api.maintenance = {
        operation: 'cleanup',
        detail: 'would delete 3 files',
        elapsedMilliseconds: 4,
        dryRun: true,
      };
      await mount();
      await maintain('Cleanup');

      expect(api.lastArgs('runMaintenance')).toEqual(['demo', 'analytics', 'cleanup', false]);
      expect(text()).toContain('Dry run — nothing was changed.');
    });

    it('commits only once the operator applies it', async () => {
      api.maintenance = {
        operation: 'cleanup',
        detail: 'would delete 3 files',
        elapsedMilliseconds: 4,
        dryRun: true,
      };
      await mount();
      await maintain('Cleanup');

      (
        fixture.nativeElement.querySelector('.dry-actions .btn-danger') as HTMLButtonElement
      ).click();
      await fixture.whenStable();

      expect(api.lastArgs('runMaintenance')).toEqual(['demo', 'analytics', 'cleanup', true]);
    });

    it('refreshes the storage panel after a committed operation', async () => {
      // Pressing Compact and watching the file count stay put is the whole reason the panel exists,
      // so the workbench reaches into it through a viewChild once the operation lands.
      api.storage = { ...api.storage, tables: [tableStorage()] };
      await mount();
      await openTab('Storage');
      const before = api.countOf('getStorage');

      await maintain('Compact');

      expect(api.countOf('getStorage')).toBe(before + 1);
    });

    it('does not refresh anything after a dry run, which changed nothing', async () => {
      api.storage = { ...api.storage, tables: [tableStorage()] };
      api.maintenance = {
        operation: 'expire',
        detail: 'would drop 2',
        elapsedMilliseconds: 1,
        dryRun: true,
      };
      await mount();
      await openTab('Storage');
      const before = api.countOf('getStorage');

      await maintain('Expire');

      expect(api.countOf('getStorage')).toBe(before);
    });

    it('refreshes the backups panel after a backup, since that is what it lists', async () => {
      await mount();
      await openTab('Backups');
      const before = api.countOf('listBackups');

      await maintain('Backup');

      expect(api.countOf('listBackups')).toBe(before + 1);
    });
  });

  describe('table restore', () => {
    it('keeps the restore in a reviewed server plan instead of placing destructive SQL in the editor', async () => {
      api.schemas = [
        { name: 'main', tables: [{ name: 'orders', kind: 'BASE TABLE', columns: [] }] },
      ];
      api.snapshots = [
        {
          snapshotId: 12,
          committedAt: '2026-07-26T00:00:00Z',
          schemaVersion: 4,
          commitMessage: null,
        },
      ];
      api.restore = {
        schema: 'main',
        table: 'orders',
        snapshotId: 12,
        currentSnapshotId: 14,
        currentRowCount: 7,
        historicalRowCount: 4,
        restoredColumns: ['id'],
        currentOnlyColumns: [],
        historicalOnlyColumns: [],
        dryRun: true,
      };

      await mount();
      await openTab('Data history');
      (fixture.nativeElement.querySelector('.restore-btn') as HTMLButtonElement).click();
      await fixture.whenStable();

      const editor = fixture.nativeElement.querySelector('.editor') as HTMLTextAreaElement;
      expect(editor.value).not.toContain('CREATE OR REPLACE TABLE');
      expect(api.lastArgs('restoreTable')).toEqual([
        'demo',
        'analytics',
        'main',
        'orders',
        12,
        false,
        null,
      ]);
      expect(fixture.nativeElement.textContent).toContain('7 rows');
      expect(fixture.nativeElement.textContent).toContain('4 historical rows');
    });
  });

  describe('changing catalog', () => {
    it('hands the new catalog to the panels', async () => {
      api.tenants = [
        {
          slug: 'demo',
          displayName: 'Demo workspace',
          catalogs: [
            { name: 'analytics', dataPath: '/a', isReadOnly: false },
            { name: 'archive', dataPath: '/b', isReadOnly: true },
          ],
        },
      ];
      api.storage = { ...api.storage, tables: [tableStorage()] };

      await mount();
      await openTab('Storage');
      expect(api.lastArgs('getStorage')).toEqual(['demo', 'analytics']);

      const select = [...fixture.nativeElement.querySelectorAll('.selectors select')].at(
        -1,
      ) as HTMLSelectElement;
      select.value = 'archive';
      select.dispatchEvent(new Event('change'));
      await fixture.whenStable();

      // The panel takes the catalog as an input and reloads itself — there is no per-panel state for
      // the workbench to remember to clear.
      expect(api.lastArgs('getStorage')).toEqual(['demo', 'archive']);
    });
  });
});
