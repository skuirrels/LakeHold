import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { LakehouseService } from './lakehouse.service';
import { TableDetail } from './models';
import { TableDetailComponent } from './table-detail.component';
import { FakeLakehouseService, tableStorage } from './test-doubles';

describe('TableDetailComponent', () => {
  let api: FakeLakehouseService;
  let fixture: ComponentFixture<TableDetailComponent>;

  async function mount() {
    fixture = TestBed.createComponent(TableDetailComponent);
    fixture.componentRef.setInput('tenant', 'demo');
    fixture.componentRef.setInput('catalog', 'analytics');
    fixture.componentRef.setInput('table', { schemaName: 'main', tableName: 'events' });
    await fixture.whenStable();
  }

  async function openSection(label: string) {
    const button = [...fixture.nativeElement.querySelectorAll('.detail-tabs button')].find(
      (candidate: Element) => candidate.textContent?.trim() === label,
    ) as HTMLButtonElement;
    button.click();
    await fixture.whenStable();
  }

  function text(): string {
    return fixture.nativeElement.textContent ?? '';
  }

  beforeEach(() => {
    api = new FakeLakehouseService();
    api.detail = {
      schemaName: 'main',
      tableName: 'events',
      kind: 'BASE TABLE',
      columns: [
        { name: 'region', dataType: 'VARCHAR', isNullable: false },
        { name: 'amount', dataType: 'DECIMAL(18,2)', isNullable: true },
      ],
      storage: tableStorage({ tableName: 'events', rowCount: 10, fileCount: 2 }),
      partitionSpecs: [
        {
          partitionId: 7,
          beginSnapshot: 3,
          endSnapshot: null,
          keys: [{ position: 0, columnName: 'region', transform: 'identity' }],
        },
      ],
      targetFileSizeBytes: null,
      advisoryFileSizeBytes: 16_000_000,
    };
    api.profile = {
      schemaName: 'main',
      tableName: 'events',
      snapshotId: null,
      rowCount: 10,
      columns: [
        {
          name: 'region',
          dataType: 'VARCHAR',
          rowCount: 10,
          nullCount: 0,
          minimum: 'north',
          maximum: 'south',
          approxDistinct: '2',
          mean: null,
          standardDeviation: null,
          firstQuartile: null,
          median: null,
          thirdQuartile: null,
        },
      ],
    };
    api.distribution = {
      schemaName: 'main',
      tableName: 'events',
      columnName: 'region',
      dataType: 'VARCHAR',
      snapshotId: null,
      kind: 'categorical',
      nullCount: 0,
      truncated: false,
      buckets: [
        { label: 'north', lowerBound: null, upperBound: null, count: 7 },
        { label: 'south', lowerBound: null, upperBound: null, count: 3 },
      ],
    };

    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: LakehouseService, useValue: api }],
    });
  });

  it('loads one coherent detail and renders its partition transform', async () => {
    await mount();

    expect(api.lastArgs('getTableDetail')).toEqual(['demo', 'analytics', 'main', 'events']);
    expect(text()).toContain('10');
    expect(text()).toContain('region');
    expect(text()).toContain('Active since snapshot 3');
  });

  it('does not compute a profile until Columns is opened', async () => {
    await mount();
    expect(api.countOf('getTableProfile')).toBe(0);

    await openSection('Columns');

    expect(api.lastArgs('getTableProfile')).toEqual([
      'demo',
      'analytics',
      'main',
      'events',
      null,
    ]);
    expect(text()).toContain('≈ distinct');
  });

  it('loads a bounded distribution only after a column is selected', async () => {
    await mount();
    await openSection('Columns');
    expect(api.countOf('getColumnDistribution')).toBe(0);

    (fixture.nativeElement.querySelector('.profiles .cell-link') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(api.lastArgs('getColumnDistribution')).toEqual([
      'demo',
      'analytics',
      'main',
      'events',
      'region',
      null,
    ]);
    expect(text()).toContain('north');
    expect(text()).toContain('south');
  });

  it('recomputes the active section at a selected snapshot', async () => {
    api.snapshots = [
      {
        snapshotId: 5,
        committedAt: '2026-07-27T12:00:00Z',
        schemaVersion: 1,
        commitMessage: null,
      },
    ];
    await mount();
    await openSection('Columns');

    const selector = fixture.nativeElement.querySelector('.snapshot-field select') as HTMLSelectElement;
    selector.value = '5';
    selector.dispatchEvent(new Event('change'));
    await fixture.whenStable();

    expect(api.lastArgs('getTableProfile')).toEqual([
      'demo',
      'analytics',
      'main',
      'events',
      5,
    ]);
  });

  it('keeps views honest by hiding files while allowing column profiles', async () => {
    api.detail = {
      ...api.detail,
      kind: 'VIEW',
      storage: null,
      partitionSpecs: [],
    };
    await mount();

    expect(text()).toContain('owns no files or partition layout');
    expect(text()).not.toContain('Files');
    expect(text()).toContain('Columns');
  });

  it('does not offer unsupported historical snapshots for a view profile', async () => {
    api.detail = {
      ...api.detail,
      kind: 'VIEW',
      storage: null,
      partitionSpecs: [],
    };
    await mount();
    await openSection('Columns');

    expect(fixture.nativeElement.querySelector('.snapshot-field')).toBeFalsy();
    expect(api.countOf('getSnapshots')).toBe(0);
    expect(api.lastArgs('getTableProfile')).toEqual([
      'demo',
      'analytics',
      'main',
      'events',
      null,
    ]);
  });

  it('drops the prior table state when its input changes', async () => {
    await mount();
    await openSection('Columns');
    expect(text()).toContain('north');

    api.detail = { ...api.detail, tableName: 'orders', columns: [] };
    api.profile = { ...api.profile, tableName: 'orders', columns: [] };
    fixture.componentRef.setInput('table', { schemaName: 'main', tableName: 'orders' });
    await fixture.whenStable();

    expect(api.lastArgs('getTableDetail')).toEqual(['demo', 'analytics', 'main', 'orders']);
    expect(text()).not.toContain('north');
    expect(text()).toContain('orders');
  });

  it('ignores a late detail response from the table it has left', async () => {
    const first = new Subject<TableDetail>();
    const second = new Subject<TableDetail>();
    let request = 0;
    api.getTableDetail = (...args: unknown[]) => {
      api.calls.push({ method: 'getTableDetail', args });
      return request++ === 0 ? first : second;
    };

    await mount();
    fixture.componentRef.setInput('table', { schemaName: 'main', tableName: 'orders' });
    await fixture.whenStable();

    second.next({
      ...api.detail,
      tableName: 'orders',
      columns: [{ name: 'current_column', dataType: 'BIGINT', isNullable: false }],
    });
    second.complete();
    await fixture.whenStable();

    first.next({
      ...api.detail,
      columns: [{ name: 'stale_column', dataType: 'BIGINT', isNullable: false }],
    });
    first.complete();
    await fixture.whenStable();

    expect(text()).toContain('current_column');
    expect(text()).not.toContain('stale_column');
  });
});
