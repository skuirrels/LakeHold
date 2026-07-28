import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, Subject } from 'rxjs';
import { DataHistoryPanelComponent } from './data-history-panel.component';
import { LakehouseService } from './lakehouse.service';
import { Snapshot, TableReference } from './models';
import { FakeLakehouseService } from './test-doubles';

describe('DataHistoryPanelComponent', () => {
  let api: FakeLakehouseService;
  let fixture: ComponentFixture<DataHistoryPanelComponent>;

  const timeline: Snapshot[] = [
    {
      snapshotId: 12,
      committedAt: '2026-07-28T10:00:00Z',
      schemaVersion: 4,
      commitMessage: 'loaded July orders',
    },
    {
      snapshotId: 10,
      committedAt: '2026-07-27T10:00:00Z',
      schemaVersion: 3,
      commitMessage: null,
    },
  ];

  async function mount(
    tables: TableReference[] = [{ schemaName: 'main', tableName: 'orders' }],
  ): Promise<void> {
    fixture = TestBed.createComponent(DataHistoryPanelComponent);
    fixture.componentRef.setInput('tenant', 'demo');
    fixture.componentRef.setInput('catalog', 'analytics');
    fixture.componentRef.setInput('tables', tables);
    fixture.componentRef.setInput('readOnly', false);
    await fixture.whenStable();
  }

  function click(selector: string): void {
    (fixture.nativeElement.querySelector(selector) as HTMLButtonElement).click();
  }

  function text(): string {
    return fixture.nativeElement.textContent ?? '';
  }

  beforeEach(() => {
    api = new FakeLakehouseService();
    api.snapshots = timeline;
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: LakehouseService, useValue: api }],
    });
  });

  it('loads a useful timeline and shows commit and schema context', async () => {
    await mount();

    expect(api.lastArgs('getSnapshots')).toEqual(['demo', 'analytics', 100]);
    expect(text()).toContain('loaded July orders');
    expect(text()).toContain('changed');
    expect(text()).toContain('latest');
  });

  it('browses historical rows inline with safely quoted catalog identifiers', async () => {
    api.queryResponse = {
      columns: [{ name: 'id', dataType: 'BigInt', clrType: 'Int64' }],
      rows: [[42]],
      truncated: false,
      elapsedMilliseconds: 2,
      rowsAffected: null,
    };
    await mount([{ schemaName: 'warm-zone', tableName: 'order.items' }]);

    click('button[aria-label*="Browse"]');
    await fixture.whenStable();

    expect(api.lastArgs('execute')).toEqual([
      'demo',
      'analytics',
      'SELECT *\nFROM "warm-zone"."order.items" AT (VERSION => 12)\nLIMIT 501;',
    ]);
    expect(text()).toContain('42');
    expect(text()).toContain('order.items at snapshot 12');
  });

  it('drills into only the selected commit for the selected table', async () => {
    api.changes = {
      schema: 'main',
      table: 'orders',
      fromSnapshot: 12,
      toSnapshot: 12,
      truncated: false,
      changes: [{ snapshotId: 12, rowId: 1, changeType: 'insert', row: { id: 7 } }],
    };
    await mount();

    click('button[aria-label*="changes in snapshot 12"]');
    await fixture.whenStable();

    expect(api.lastArgs('getChanges')).toEqual(['demo', 'analytics', 'main', 'orders', 12, 12]);
    expect(text()).toContain('changes in snapshot 12');
    expect(text()).toContain('insert');
  });

  it('compares state after the older baseline through the newer target', async () => {
    api.changes = {
      ...api.changes,
      fromSnapshot: 11,
      toSnapshot: 12,
      changes: [],
    };
    await mount();

    click('.history-controls .btn-primary');
    await fixture.whenStable();

    // The CDC function is inclusive, so comparing state at 10 with state at 12 starts at 11.
    expect(api.lastArgs('getChanges')).toEqual(['demo', 'analytics', 'main', 'orders', 11, 12]);
    expect(text()).toContain('after snapshot 10 through 12');
    expect(text()).toContain('No row-level changes');
  });

  it('reviews and confirms an atomic restore for the exact selected table', async () => {
    api.restore = {
      schema: 'warm-zone',
      table: 'order.items',
      snapshotId: 12,
      currentSnapshotId: 18,
      currentRowCount: 8,
      historicalRowCount: 3,
      restoredColumns: ['id', 'status'],
      currentOnlyColumns: ['region'],
      historicalOnlyColumns: ['legacy_code'],
      dryRun: true,
    };
    await mount([{ schemaName: 'warm-zone', tableName: 'order.items' }]);

    click('button[aria-label*="Review restore"]');
    await fixture.whenStable();

    expect(api.countOf('execute')).toBe(0);
    expect(api.lastArgs('restoreTable')).toEqual([
      'demo',
      'analytics',
      'warm-zone',
      'order.items',
      12,
      false,
      null,
    ]);
    expect(text()).toContain('8 rows');
    expect(text()).toContain('3 historical rows');
    expect(text()).toContain('region');
    expect(text()).toContain('legacy_code');

    api.restore = { ...api.restore, dryRun: false };
    click('button.btn-danger');
    await fixture.whenStable();

    expect(api.lastArgs('restoreTable')).toEqual([
      'demo',
      'analytics',
      'warm-zone',
      'order.items',
      12,
      true,
      18,
    ]);
    expect(text()).toContain('Restored warm-zone.order.items to 3 rows from snapshot 12.');
  });

  it('keeps restore controls out of a read-only history surface', async () => {
    await mount();
    fixture.componentRef.setInput('readOnly', true);
    await fixture.whenStable();

    expect(fixture.nativeElement.querySelector('button[aria-label*="Review restore"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('button[aria-label*="Browse"]')).toBeTruthy();
  });

  it('marks the bounded historical preview when a 501st row exists', async () => {
    api.queryResponse = {
      columns: [{ name: 'id', dataType: 'BigInt', clrType: 'Int64' }],
      rows: Array.from({ length: 501 }, (_, index) => [index + 1]),
      truncated: false,
      elapsedMilliseconds: 2,
      rowsAffected: null,
    };
    await mount();

    click('button[aria-label*="Browse"]');
    await fixture.whenStable();

    expect(text()).toContain('500 rows');
    expect(text()).toContain('truncated');
    expect(fixture.nativeElement.querySelectorAll('lh-result-grid tbody tr')).toHaveLength(500);
  });

  it('reports a historical read failure as a panel-local error', async () => {
    api.failures.set('execute', 'table did not exist at snapshot 10');
    await mount();

    const browseButtons = fixture.nativeElement.querySelectorAll(
      'button[aria-label*="Browse"]',
    ) as NodeListOf<HTMLButtonElement>;
    browseButtons[1].click();
    await fixture.whenStable();

    expect(text()).toContain('Could not read main.orders at snapshot 10');
    expect(text()).toContain('table did not exist at snapshot 10');
  });

  it('does not render a historical response after the operator selects another table', async () => {
    const pending = new Subject<typeof api.queryResponse>();
    vi.spyOn(api, 'execute').mockReturnValue(pending);
    await mount([
      { schemaName: 'main', tableName: 'orders' },
      { schemaName: 'main', tableName: 'customers' },
    ]);

    click('button[aria-label*="Browse"]');
    const table = fixture.nativeElement.querySelector(
      '[aria-label="History table"]',
    ) as HTMLSelectElement;
    table.value = '1';
    table.dispatchEvent(new Event('change'));
    await fixture.whenStable();

    pending.next({
      columns: [{ name: 'status', dataType: 'Varchar', clrType: 'String' }],
      rows: [['stale orders response']],
      truncated: false,
      elapsedMilliseconds: 1,
      rowsAffected: null,
    });
    pending.complete();
    await fixture.whenStable();

    expect(text()).not.toContain('stale orders response');
    expect(text()).toContain('main.customers');
  });

  it('clears and invalidates historical detail as soon as the catalog changes', async () => {
    const historical = new Subject<typeof api.queryResponse>();
    const nextTimeline = new Subject<Snapshot[]>();
    vi.spyOn(api, 'getSnapshots')
      .mockReturnValueOnce(of(timeline))
      .mockReturnValueOnce(nextTimeline);
    vi.spyOn(api, 'execute').mockReturnValue(historical);
    await mount();

    click('button[aria-label*="Browse"]');
    fixture.componentRef.setInput('catalog', 'finance');
    await fixture.whenStable();

    historical.next({
      columns: [{ name: 'secret', dataType: 'Varchar', clrType: 'String' }],
      rows: [['previous catalog row']],
      truncated: false,
      elapsedMilliseconds: 1,
      rowsAffected: null,
    });
    historical.complete();
    await fixture.whenStable();

    expect(text()).not.toContain('previous catalog row');
    expect(text()).not.toContain('main.orders at snapshot 12');
    expect(text()).toContain('Loading catalog history');
  });
});
