import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { ApiError, LakehouseService } from './lakehouse.service';
import { FakeLakehouseService, tableStorage } from './test-doubles';
import { WorkbenchComponent } from './workbench.component';

describe('WorkbenchComponent', () => {
  let api: FakeLakehouseService;
  let fixture: ComponentFixture<WorkbenchComponent>;

  async function mount() {
    fixture = TestBed.createComponent(WorkbenchComponent);
    await fixture.whenStable();
    return fixture;
  }

  function text(): string {
    return fixture.nativeElement.textContent ?? '';
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
    api = new FakeLakehouseService();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: LakehouseService, useValue: api },
      ],
    });
  });

  it('selects the first workspace and catalog it is given', async () => {
    await mount();

    expect(api.lastArgs('getSchemas')).toEqual(['demo', 'analytics']);
    expect(text()).toContain('Demo workspace');
  });

  describe('first run', () => {
    it('shows sign-in instead of an error when the deployment requires a credential', async () => {
      api.errors.set('getAccess', new ApiError('Unauthorized', 401));

      await mount();

      expect(text()).toContain('Sign in to this Lakehold node');
      expect(text()).not.toContain('docker compose');
      expect(text()).not.toContain('Could not load workspaces');
      expect(fixture.nativeElement.querySelector('.tabs')).toBeNull();
    });

    it('opens a friendly read-only demo without exposing mutation controls', async () => {
      api.access = { mode: 'demo', role: 'reader', readOnly: true };
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

      expect(text()).toContain('You’re exploring a live Lakehold demo');
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
        ['createToken', ['northwind', 'workbench', 'owner']],
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
  });

  describe('errors', () => {
    it('names the operation rather than always blaming a query', async () => {
      api.failures.set('getSchemas', 'catalog is gone');
      await mount();

      expect(text()).toContain('Could not load the catalog');
      expect(text()).toContain('catalog is gone');
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

  describe('snapshot restore', () => {
    it('loads a reversible per-table statement into the editor', async () => {
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

      await mount();
      await openTab('Snapshots');
      (fixture.nativeElement.querySelector('.restore-btn') as HTMLButtonElement).click();
      await fixture.whenStable();

      const editor = fixture.nativeElement.querySelector('.editor') as HTMLTextAreaElement;

      // CREATE OR REPLACE ... AT (VERSION => n) records a *new* snapshot rather than rewriting
      // history, which is what keeps the restore itself reversible.
      expect(editor.value).toContain('CREATE OR REPLACE TABLE main.orders');
      expect(editor.value).toContain('AT (VERSION => 12)');
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
