import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { of, Subject } from 'rxjs';
import { ApiError, LakehouseService } from './lakehouse.service';
import { FakeLakehouseService, tableStorage } from './test-doubles';
import { WorkbenchComponent } from './workbench.component';
import { QueryEditorComponent } from './query-editor.component';
import { WorkbenchNavigationComponent } from './workbench-navigation.component';

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

  function setEditorValue(value: string): void {
    const editor = fixture.debugElement.query(By.directive(QueryEditorComponent))
      .componentInstance as QueryEditorComponent;
    editor.valueChange.emit(value);
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

  /** The workspace or catalog picker, found by the label beside it. */
  function selector(label: string): HTMLSelectElement {
    const field = [...fixture.nativeElement.querySelectorAll('.selectors .field')].find(
      (f) => (f as HTMLElement).querySelector('span')?.textContent?.trim() === label,
    ) as HTMLElement;
    return field.querySelector('select') as HTMLSelectElement;
  }

  /** What a picker actually shows — the option the browser has selected, not the bound value. */
  function selectedLabel(select: HTMLSelectElement): string {
    return select.options[select.selectedIndex]?.textContent?.trim() ?? '';
  }

  /** Drives the product navigation, which is how a system administrator reaches the editor. */
  async function navigateTo(destination: string): Promise<void> {
    const navigation = fixture.debugElement.query(By.directive(WorkbenchNavigationComponent))
      .componentInstance as WorkbenchNavigationComponent;
    navigation.navigate.emit(destination as never);
    await fixture.whenStable();
  }

  /** Clicks a maintenance button by its label. */
  async function maintain(label: string): Promise<void> {
    (fixture.nativeElement.querySelector('.maintenance-menu > .btn') as HTMLButtonElement).click();
    await fixture.whenStable();
    const button = [...fixture.nativeElement.querySelectorAll('.maintenance-popover button')].find(
      (b) => (b as HTMLElement).querySelector('strong')?.textContent?.trim() === label,
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
      tenantAdmin: true,
    };

    await mount();

    expect(text()).toContain('System Settings');
    expect(fixture.nativeElement.querySelector('lh-system-settings')).toBeTruthy();
    expect(api.countOf('getSchemas')).toBe(0);
  });

  it('administers users from their own destination, not from System Settings', async () => {
    api.access = {
      mode: 'authenticated',
      role: 'owner',
      readOnly: false,
      systemAdmin: true,
      tenantAdmin: true,
    };

    await mount();

    // Users and tokens answer to a workspace credential and settings to an instance one. Sharing a
    // page put a card an owner is refused above the two they administer.
    expect(fixture.nativeElement.querySelector('lh-system-settings')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('lh-member-administration')).toBeNull();
    expect(fixture.nativeElement.querySelector('lh-token-administration')).toBeNull();

    await navigateTo('users');

    expect(fixture.nativeElement.querySelector('lh-system-settings')).toBeNull();
    expect(fixture.nativeElement.querySelector('lh-member-administration')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('lh-token-administration')).toBeTruthy();
  });

  it('offers a workspace owner Users but not the instance-only settings page', async () => {
    api.access = {
      mode: 'authenticated',
      role: 'owner',
      readOnly: false,
      systemAdmin: false,
      tenantAdmin: true,
    };

    await mount();

    const labels = [
      ...fixture.nativeElement.querySelectorAll('lh-workbench-navigation .nav-item'),
    ].map((item) => (item as HTMLElement).getAttribute('aria-label'));
    // Every control on System Settings requires Capability.Instance, so offering the destination to
    // an owner offers a page whose one card returns an error.
    expect(labels).toContain('Users');
    expect(labels).not.toContain('System Settings');
  });

  it('leaves Users when the credential that administered it is replaced by a reader', async () => {
    api.access = {
      mode: 'authenticated',
      role: 'owner',
      readOnly: false,
      systemAdmin: false,
      tenantAdmin: true,
    };
    await mount();
    await navigateTo('users');
    expect(fixture.nativeElement.querySelector('lh-member-administration')).toBeTruthy();

    api.access = {
      mode: 'authenticated',
      role: 'reader',
      readOnly: true,
      systemAdmin: false,
      tenantAdmin: false,
    };
    (fixture.nativeElement.querySelector('.credential > .btn') as HTMLButtonElement).click();
    await fixture.whenStable();
    const token = fixture.nativeElement.querySelector(
      '.credential input[type="password"]',
    ) as HTMLInputElement;
    token.value = 'lkh_reader_replacement';
    token.dispatchEvent(new Event('input'));
    (
      fixture.nativeElement.querySelector('.credential-actions .btn-primary') as HTMLButtonElement
    ).click();
    await fixture.whenStable();

    // A reader administers nobody. Left where it was, the destination shows a member list whose every
    // request is refused, and the navigation no longer offers a way back to it.
    expect(fixture.nativeElement.querySelector('lh-member-administration')).toBeNull();
    expect(fixture.nativeElement.querySelector('[aria-label="SQL editor"]')).toBeTruthy();
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
      tenantAdmin: true,
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
      tenantAdmin: true,
    };
    api.tenants = [];

    await mount();

    expect(fixture.nativeElement.querySelector('lh-system-settings')).toBeNull();
    expect(text()).toContain('No workspaces yet');
  });

  it('does not report a workspace as selected when an instance administrator has none', async () => {
    api.access = {
      mode: 'authenticated',
      role: 'owner',
      readOnly: false,
      systemAdmin: true,
      tenantAdmin: true,
    };

    await mount();
    await navigateTo('workbench');

    // The regression: a select whose value matches no option displays its first option, so the
    // picker read "Demo workspace" while the component held no workspace at all — and the empty
    // catalog beside it looked like a catalog that had disappeared.
    const workspace = selector('Workspace');
    expect(workspace.value).toBe('');
    expect(selectedLabel(workspace)).toBe('Administration only');
    expect(selectedLabel(selector('Catalog'))).toBe('Administration only');
    expect(text()).toContain('You’re signed in as an instance administrator.');
  });

  it('names the selected workspace and catalog for a tenant credential', async () => {
    await mount();

    expect(selector('Workspace').value).toBe('demo');
    expect(selectedLabel(selector('Workspace'))).toBe('Demo workspace');
    expect(selector('Catalog').value).toBe('analytics');
    expect(selectedLabel(selector('Catalog'))).toBe('analytics');
    expect(text()).not.toContain('You’re signed in as an instance administrator.');
  });

  it('returns to the workbench when an instance credential is replaced by a tenant credential', async () => {
    api.access = {
      mode: 'authenticated',
      role: 'owner',
      readOnly: false,
      systemAdmin: true,
      tenantAdmin: true,
    };
    await mount();
    expect(fixture.nativeElement.querySelector('lh-system-settings')).toBeTruthy();

    api.access = {
      mode: 'authenticated',
      role: 'owner',
      readOnly: false,
      systemAdmin: false,
      tenantAdmin: true,
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

  it('discovers LINQ, keeps a separate editor buffer, and submits authored source by language', async () => {
    api.queryLanguages.push({
      id: 'csharp-linq',
      displayName: 'C# LINQ',
      editorLanguage: 'csharp',
      starterSource: 'from row in Main.Events select row',
      readOnly: true,
      supportsSavedQueries: true,
    });
    api.queryStarters.set('csharp-linq', {
      source: 'from row in _123Data.OrderItems select row',
      schemaFingerprint: 'schema-1',
    });
    await mount();

    const language = fixture.nativeElement.querySelector(
      '.language-picker select',
    ) as HTMLSelectElement;
    language.value = 'csharp-linq';
    language.dispatchEvent(new Event('change'));
    await fixture.whenStable();

    const editor = fixture.nativeElement.querySelector(
      '[aria-label="C# LINQ editor"]',
    ) as HTMLElement;
    expect(editor.textContent).toBe('from row in _123Data.OrderItems select row');
    expect(api.lastArgs('getQueryStarter')).toEqual(['demo', 'analytics', 'csharp-linq']);
    setEditorValue('Main.Events.Count()');
    await fixture.whenStable();

    (
      fixture.nativeElement.querySelector('.editor-toolbar .btn-primary') as HTMLButtonElement
    ).click();
    await fixture.whenStable();
    expect(api.lastArgs('execute')).toEqual([
      'demo',
      'analytics',
      'Main.Events.Count()',
      'csharp-linq',
    ]);

    language.value = 'sql';
    language.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(
      (fixture.nativeElement.querySelector('[aria-label="SQL editor"]') as HTMLElement).textContent,
    ).toContain('SELECT');
  });

  it('keeps an unhealthy planner in the selector and says why it cannot run', async () => {
    // The API reports a configured planner that failed discovery instead of omitting it: omitting
    // it is indistinguishable from never having installed the language.
    api.queryLanguages.push({
      id: 'csharp-linq',
      displayName: 'C# LINQ',
      editorLanguage: 'csharp',
      starterSource: 'from row in Main.Events select row',
      readOnly: true,
      supportsSavedQueries: true,
      available: false,
      unavailableReason: 'The planner did not answer within the 1s discovery deadline.',
    });
    await mount();

    const language = fixture.nativeElement.querySelector(
      '.language-picker select',
    ) as HTMLSelectElement;
    const option = [...language.options].find((candidate) => candidate.value === 'csharp-linq')!;
    expect(option.textContent?.trim()).toBe('C# LINQ (unavailable)');
    expect(option.title).toBe('The planner did not answer within the 1s discovery deadline.');

    language.value = 'csharp-linq';
    language.dispatchEvent(new Event('change'));
    await fixture.whenStable();

    expect(text()).toContain(
      'Planner unavailable — The planner did not answer within the 1s discovery deadline.',
    );
    const run = fixture.nativeElement.querySelector(
      '.editor-toolbar .btn-primary',
    ) as HTMLButtonElement;
    expect(run.disabled).toBe(true);
    run.click();
    expect(api.countOf('execute')).toBe(0);
    // Nothing was asked of the planner that is down.
    expect(api.countOf('getQueryStarter')).toBe(0);
  });

  it('preserves source for an unavailable language without executing or resaving it as SQL', async () => {
    await mount();

    (
      fixture.componentInstance as unknown as {
        openSource(query: { language: string; source: string }): void;
      }
    ).openSource({ language: 'legacy-linq', source: 'Legacy.Events.Where(e => e.Active)' });
    await fixture.whenStable();

    const editor = fixture.nativeElement.querySelector(
      '[aria-label="legacy-linq (unavailable) editor"]',
    ) as HTMLElement;
    const run = fixture.nativeElement.querySelector(
      '.editor-toolbar .btn-primary',
    ) as HTMLButtonElement;
    expect(editor.textContent).toBe('Legacy.Events.Where(e => e.Active)');
    expect(run.disabled).toBe(true);
    run.click();
    expect(api.countOf('execute')).toBe(0);

    await navigateTo('queries');
    expect(
      (fixture.nativeElement.querySelector('.query-head button') as HTMLButtonElement).disabled,
    ).toBe(true);
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

      toggle.click();
      await fixture.whenStable();

      expect(
        (
          fixture.nativeElement.querySelector(
            '[aria-label="Filter catalog objects"]',
          ) as HTMLInputElement
        ).value,
      ).toBe('events');
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
      expect(fixture.nativeElement.querySelector('.destination-page h1')?.textContent?.trim()).toBe(
        'Query history',
      );
    });

    it('uses an Add data adapter choice for one connector handoff only', async () => {
      await mount();
      await navigateTo('add-data');

      const search = fixture.nativeElement.querySelector(
        '.source-search input',
      ) as HTMLInputElement;
      search.value = 'hubspot';
      search.dispatchEvent(new Event('input'));
      await fixture.whenStable();
      (fixture.nativeElement.querySelector('.configure') as HTMLButtonElement).click();
      await fixture.whenStable();

      expect(
        (fixture.nativeElement.querySelector('.panel.editor select') as HTMLSelectElement).value,
      ).toBe('hubspot');

      await navigateTo('workbench');
      await navigateTo('connectors');

      expect(fixture.nativeElement.querySelector('.panel.editor')).toBeNull();
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
      api.access = {
        mode: 'demo',
        role: 'reader',
        readOnly: true,
        systemAdmin: false,
        tenantAdmin: false,
      };
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
      // The escape hatch from the demo is a pasted operator token, so it is labelled as one. It was
      // "Operator sign in", which described a login the demo has no way to perform.
      expect(text()).toContain('Operator token');
      expect(fixture.nativeElement.querySelector('.maintenance-menu')).toBeNull();
      expect(fixture.nativeElement.querySelector('[aria-label="SQL editor"]')).not.toBeNull();

      await navigateTo('changes');
      expect(text()).not.toContain('New subscription');

      await navigateTo('backups');
      expect(text()).not.toContain('Restore…');

      await navigateTo('ejects');
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
        // No placement: an operator who touched nothing in the storage section still provisions
        // through the deployment's default, which is the one-click path this test guards.
        ['createCatalog', ['northwind', 'warehouse', undefined]],
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
      await navigateTo('changes');

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

      expect(fixture.nativeElement.querySelector('lh-storage-panel')).toBeTruthy();
      expect(
        fixture.nativeElement.querySelector('[aria-label="Storage"]')?.classList.contains('active'),
      ).toBe(true);
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
          catalogs: [
            {
              name: 'current_catalog',
              dataPath: '/current',
              isReadOnly: false,
              storageKind: 'Local',
              storageProfile: null,
            },
          ],
        },
      ];
      const staleTenants: typeof api.tenants = [
        {
          slug: 'stale',
          displayName: 'Stale workspace',
          catalogs: [
            {
              name: 'stale_catalog',
              dataPath: '/stale',
              isReadOnly: false,
              storageKind: 'Local',
              storageProfile: null,
            },
          ],
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

      await navigateTo('schedule');
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
      await maintain('Compact');
      await navigateTo('storage');
      expect(api.countOf('getStorage')).toBe(1);
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
      await maintain('Expire');
      expect(api.countOf('getStorage')).toBe(0);
    });

    it('refreshes the backups panel after a backup, since that is what it lists', async () => {
      await mount();
      await maintain('Backup');
      await navigateTo('backups');
      expect(api.countOf('listBackups')).toBe(1);
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
      await navigateTo('snapshots');
      (fixture.nativeElement.querySelector('.restore-btn') as HTMLButtonElement).click();
      await fixture.whenStable();

      expect(fixture.nativeElement.querySelector('.cm-content')).toBeNull();
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
    it('scopes query history to the selected catalog', async () => {
      const run = (id: number, catalogName: string, sql: string) => ({
        id,
        catalogName,
        sql,
        language: 'sql',
        startedUtc: '2026-08-21T12:00:00Z',
        elapsedMilliseconds: 1,
        rowCount: 1,
        succeeded: true,
        error: null,
        tokenId: null,
        tokenName: null,
        memberId: null,
        actorKind: 'Unknown' as const,
        actorName: null,
        origin: 'Workbench' as const,
      });
      api.history = [
        run(1, 'analytics', 'select analytics_only'),
        run(2, 'archive', 'select archive_only'),
      ];
      api.tenants = [
        {
          slug: 'demo',
          displayName: 'Demo workspace',
          catalogs: [
            {
              name: 'analytics',
              dataPath: '/a',
              isReadOnly: false,
              storageKind: 'Local',
              storageProfile: null,
            },
            {
              name: 'archive',
              dataPath: '/b',
              isReadOnly: true,
              storageKind: 'Local',
              storageProfile: null,
            },
          ],
        },
      ];

      await mount();
      await navigateTo('history');
      expect(text()).toContain('analytics_only');
      expect(text()).not.toContain('archive_only');

      const catalog = fixture.nativeElement.querySelectorAll(
        '.destination-context select',
      )[1] as HTMLSelectElement;
      catalog.value = 'archive';
      catalog.dispatchEvent(new Event('change'));
      await fixture.whenStable();
      expect(text()).toContain('archive_only');
      expect(text()).not.toContain('analytics_only');
    });

    it('hands the new catalog to the panels', async () => {
      api.tenants = [
        {
          slug: 'demo',
          displayName: 'Demo workspace',
          catalogs: [
            {
              name: 'analytics',
              dataPath: '/a',
              isReadOnly: false,
              storageKind: 'Local',
              storageProfile: null,
            },
            {
              name: 'archive',
              dataPath: '/b',
              isReadOnly: true,
              storageKind: 'Local',
              storageProfile: null,
            },
          ],
        },
      ];
      api.storage = { ...api.storage, tables: [tableStorage()] };

      await mount();
      await navigateTo('storage');
      expect(api.lastArgs('getStorage')).toEqual(['demo', 'analytics']);
      const historyRequests = api.countOf('getHistory');

      const select = [...fixture.nativeElement.querySelectorAll('.destination-context select')].at(
        -1,
      ) as HTMLSelectElement;
      select.value = 'archive';
      select.dispatchEvent(new Event('change'));
      await fixture.whenStable();

      // The panel takes the catalog as an input and reloads itself — there is no per-panel state for
      // the workbench to remember to clear.
      expect(api.lastArgs('getStorage')).toEqual(['demo', 'archive']);
      expect(api.lastArgs('getHistory')).toEqual(['demo']);
      expect(api.countOf('getHistory')).toBe(historyRequests + 1);
    });
  });

  describe('credential affordance', () => {
    it('never calls the token box a sign-in', async () => {
      api.browserSession = {
        oidcEnabled: false,
        authenticated: false,
        displayName: null,
        systemAdmin: false,
      };

      await mount();

      const control = fixture.nativeElement.querySelector(
        '.credential button',
      ) as HTMLButtonElement;
      // "Sign in" promises an account. This control takes a machine token and there is no identity
      // provider configured, so calling it a sign-in describes something the node cannot do.
      expect(control.textContent?.trim()).toBe('API token');
      expect(fixture.nativeElement.textContent).not.toContain('Sign in');

      control.click();
      await fixture.whenStable();

      // Every node requires a credential now, so the panel always says where the first one comes
      // from rather than sometimes announcing that none is needed.
      expect(fixture.nativeElement.textContent).toContain('lkh_admin_');
    });

    it('offers a real sign-in only when an identity provider is configured', async () => {
      api.browserSession = {
        oidcEnabled: true,
        authenticated: false,
        displayName: null,
        systemAdmin: false,
      };

      await mount();

      const login = fixture.nativeElement.querySelector(
        'a[href^="/auth/login"]',
      ) as HTMLAnchorElement;
      expect(login).toBeTruthy();
      expect(login.textContent?.trim()).toBe('Sign in');

      // The token box stays available beside it, still labelled for what it is.
      const control = fixture.nativeElement.querySelector(
        '.credential button',
      ) as HTMLButtonElement;
      expect(control.textContent?.trim()).toBe('API token');
    });

    it('points at the bootstrap token when the node is gated and holds no credential', async () => {
      api.browserSession = {
        oidcEnabled: false,
        authenticated: false,
        displayName: null,
        systemAdmin: false,
      };

      await mount();

      (fixture.nativeElement.querySelector('.credential button') as HTMLButtonElement).click();
      await fixture.whenStable();

      expect(fixture.nativeElement.textContent).toContain('lkh_admin_');
      expect(fixture.nativeElement.textContent).not.toContain('does not require a credential');
    });
  });
});
