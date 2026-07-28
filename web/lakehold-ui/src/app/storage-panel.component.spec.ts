import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { Subject } from 'rxjs';
import { LakehouseService } from './lakehouse.service';
import { CatalogStorage } from './models';
import { StoragePanelComponent } from './storage-panel.component';
import { FakeLakehouseService, tableStorage } from './test-doubles';

describe('StoragePanelComponent', () => {
  let api: FakeLakehouseService;
  let fixture: ComponentFixture<StoragePanelComponent>;

  async function mount(tenant: string | null = 'demo', catalog: string | null = 'analytics') {
    fixture = TestBed.createComponent(StoragePanelComponent);
    fixture.componentRef.setInput('tenant', tenant);
    fixture.componentRef.setInput('catalog', catalog);
    await fixture.whenStable();
    return fixture;
  }

  function text(): string {
    return fixture.nativeElement.textContent ?? '';
  }

  beforeEach(() => {
    api = new FakeLakehouseService();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: LakehouseService, useValue: api }],
    });
  });

  it('reads the footprint for the catalog it is pointed at', async () => {
    api.storage = { ...api.storage, tables: [tableStorage({ tableName: 'events', rowCount: 250_000 })] };
    await mount();

    expect(api.lastArgs('getStorage')).toEqual(['demo', 'analytics']);
    expect(text()).toContain('events');
    expect(text()).toContain((250_000).toLocaleString());
  });

  it('re-reads when the catalog changes, and not otherwise', async () => {
    await mount();
    expect(api.countOf('getStorage')).toBe(1);

    fixture.componentRef.setInput('catalog', 'other');
    await fixture.whenStable();

    expect(api.countOf('getStorage')).toBe(2);
    expect(api.lastArgs('getStorage')).toEqual(['demo', 'other']);
  });

  it('ignores a late response from the catalog it has left', async () => {
    const first = new Subject<CatalogStorage>();
    const second = new Subject<CatalogStorage>();
    let request = 0;
    api.getStorage = (...args: unknown[]) => {
      api.calls.push({ method: 'getStorage', args });
      return request++ === 0 ? first : second;
    };

    await mount();
    fixture.componentRef.setInput('catalog', 'other');
    await fixture.whenStable();

    second.next({
      ...api.storage,
      tables: [tableStorage({ tableName: 'current_catalog_table' })],
    });
    second.complete();
    await fixture.whenStable();

    first.next({
      ...api.storage,
      tables: [tableStorage({ tableName: 'stale_catalog_table' })],
    });
    first.complete();
    await fixture.whenStable();

    expect(text()).toContain('current_catalog_table');
    expect(text()).not.toContain('stale_catalog_table');
  });

  it('asks for nothing until it has a catalog', async () => {
    await mount('demo', null);
    expect(api.countOf('getStorage')).toBe(0);
  });

  it('can inspect a view even when the catalog owns no base-table storage', async () => {
    api.storage = { ...api.storage, tables: [] };
    api.detail = {
      schemaName: 'main',
      tableName: 'current_events',
      kind: 'VIEW',
      columns: [{ name: 'id', dataType: 'BIGINT', isNullable: true }],
      storage: null,
      partitionSpecs: [],
      targetFileSizeBytes: null,
      advisoryFileSizeBytes: 16_000_000,
    };

    fixture = TestBed.createComponent(StoragePanelComponent);
    fixture.componentRef.setInput('tenant', 'demo');
    fixture.componentRef.setInput('catalog', 'analytics');
    fixture.componentRef.setInput('inspect', {
      schemaName: 'main',
      tableName: 'current_events',
    });
    await fixture.whenStable();

    expect(text()).toContain('This catalog has no tables yet');
    expect(fixture.nativeElement.querySelector('.table-detail')).toBeTruthy();
    expect(text()).toContain('current_events');
  });

  describe('opening a table', () => {
    beforeEach(() => {
      api.storage = {
        ...api.storage,
        tables: [tableStorage({ schemaName: 'warm', tableName: 'sessions' })],
      };
      api.files = {
        schemaName: 'warm',
        tableName: 'sessions',
        snapshotId: null,
        truncated: false,
        files: [
          {
            dataFile: '/data/warm/sessions/ducklake-a.parquet',
            dataFileSizeBytes: 8_800,
            deleteFile: null,
            deleteFileSizeBytes: null,
          },
        ],
      };
      api.detail = {
        schemaName: 'warm',
        tableName: 'sessions',
        kind: 'BASE TABLE',
        columns: [{ name: 'id', dataType: 'BIGINT', isNullable: false }],
        storage: api.storage.tables[0],
        partitionSpecs: [],
        targetFileSizeBytes: null,
        advisoryFileSizeBytes: 16_000_000,
      };
    });

    async function openFiles() {
      (fixture.nativeElement.querySelector('button.cell-link') as HTMLButtonElement).click();
      await fixture.whenStable();
      const files = [...fixture.nativeElement.querySelectorAll('.detail-tabs button')].find(
        (button: Element) => button.textContent?.trim() === 'Files',
      ) as HTMLButtonElement;
      files.click();
      await fixture.whenStable();
    }

    /**
     * The regression this suite exists for.
     *
     * The panel reloads from an effect reading its `tenant` and `catalog` inputs. When that effect
     * also called `reload()` — which reads `selectedTable` — the signal became a dependency of the
     * effect, so opening a table re-ran it and closed the table again. The detail panel simply never
     * appeared. It passed the type checker and the production build; only clicking revealed it.
     */
    it('stays open — the effect must not depend on what the reload reads', async () => {
      await mount();

      const open = fixture.nativeElement.querySelector('button.cell-link') as HTMLButtonElement;
      expect(open).toBeTruthy();

      open.click();
      await fixture.whenStable();

      expect(fixture.nativeElement.querySelector('.table-detail')).toBeTruthy();
      expect(text()).toContain('Overview');
    });

    it('loads the file list for the table that was clicked', async () => {
      await mount();
      await openFiles();

      expect(api.lastArgs('getTableFiles')).toEqual(['demo', 'analytics', 'warm', 'sessions', null]);
      expect(text()).toContain('ducklake-a.parquet');
    });

    it('loads snapshots so the as-of selector has something to offer', async () => {
      await mount();
      await openFiles();

      expect(api.countOf('getSnapshots')).toBe(1);
    });

    it('closes the detail panel when the catalog changes underneath it', async () => {
      await mount();
      (fixture.nativeElement.querySelector('button.cell-link') as HTMLButtonElement).click();
      await fixture.whenStable();
      expect(fixture.nativeElement.querySelector('.table-detail')).toBeTruthy();

      fixture.componentRef.setInput('catalog', 'other');
      await fixture.whenStable();

      // Leaving one catalog's files on screen under another catalog's name is a wrong readout.
      expect(fixture.nativeElement.querySelector('.table-detail')).toBeFalsy();
    });
  });

  describe('advisories', () => {
    async function advisoryFor(overrides: Parameters<typeof tableStorage>[0]) {
      api.storage = { ...api.storage, tables: [tableStorage(overrides)] };
      await mount();
      return fixture.nativeElement.querySelector('.advisory')?.textContent?.trim() ?? null;
    }

    it('names a pending flush', async () => {
      expect(await advisoryFor({ needsFlush: true, inlinedRows: 7 })).toBe('Flush pending');
    });

    it('names fragmentation', async () => {
      expect(await advisoryFor({ needsCompaction: true })).toBe('Fragmented');
    });

    it('names both when both apply', async () => {
      expect(await advisoryFor({ needsFlush: true, needsCompaction: true })).toBe('Flush and compact');
    });

    it('says nothing when the table needs nothing', async () => {
      expect(await advisoryFor({})).toBeNull();
    });

    it('marks a pending flush as information rather than a warning', async () => {
      // Rows are safely committed, just not Parquet yet. Colouring it like fragmentation would train
      // the operator to ignore both.
      api.storage = { ...api.storage, tables: [tableStorage({ needsFlush: true })] };
      await mount();
      expect(fixture.nativeElement.querySelector('.advisory')?.classList).toContain('advisory-flush');
    });
  });

  describe('the advisory threshold note', () => {
    it("names the catalog's own target when it has one", async () => {
      api.storage = { ...api.storage, tables: [tableStorage()], targetFileSizeBytes: 5_000_000 };
      await mount();
      expect(text()).toContain("this catalog's target file size of 5.0 MB");
    });

    it("falls back to the deployment's floor when it has none", async () => {
      api.storage = { ...api.storage, tables: [tableStorage()], targetFileSizeBytes: null };
      await mount();
      expect(text()).toContain('advisory floor of 16 MB');
    });
  });

  describe('failures', () => {
    it('names the operation that failed rather than blaming a query', async () => {
      api.failures.set('getStorage', 'boom');
      await mount();

      expect(text()).toContain('Could not read storage');
      expect(text()).toContain('boom');
      expect(text()).not.toContain('This catalog has no tables yet');
    });

    it('does not carry a failure across to another catalog', async () => {
      api.failures.set('getStorage', 'boom');
      await mount();
      expect(text()).toContain('boom');

      api.failures.clear();
      fixture.componentRef.setInput('catalog', 'other');
      await fixture.whenStable();

      // The panel is not destroyed by a catalog change the way it is by a tab change, so the banner
      // it owns has to be cleared deliberately. A failure left standing over a catalog that answered
      // fine says the wrong thing about the catalog now on screen.
      expect(text()).not.toContain('boom');
    });

    it('does not leave one catalog rows on screen under another catalog name', async () => {
      api.storage = { ...api.storage, tables: [tableStorage({ tableName: 'events' })] };
      await mount();
      expect(text()).toContain('events');

      api.failures.set('getStorage', 'boom');
      fixture.componentRef.setInput('catalog', 'other');
      await fixture.whenStable();

      // The second catalog could not be read, so there is nothing true to show for it. Leaving the
      // first one's tables under the second one's name is a wrong readout, not a cautious one.
      expect(text()).not.toContain('events');
    });

    it('distinguishes a file-list failure from a rollup failure', async () => {
      // A snapshot predating the table is refused by the engine, and the message is worth showing —
      // but under its own heading, not the rollup's.
      api.storage = { ...api.storage, tables: [tableStorage()] };
      api.detail = {
        ...api.detail,
        tableName: 'events',
        storage: api.storage.tables[0],
      };
      await mount();

      api.failures.set('getTableFiles', 'does not exist at version 0');
      (fixture.nativeElement.querySelector('button.cell-link') as HTMLButtonElement).click();
      await fixture.whenStable();
      const files = [...fixture.nativeElement.querySelectorAll('.detail-tabs button')].find(
        (button: Element) => button.textContent?.trim() === 'Files',
      ) as HTMLButtonElement;
      files.click();
      await fixture.whenStable();

      expect(text()).toContain('Could not list files');
      expect(text()).toContain('does not exist at version 0');
    });
  });
});
