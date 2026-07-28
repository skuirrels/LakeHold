import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { ChangeGridComponent } from './change-grid.component';
import { formatTime, quoteTable } from './format';
import { LakehouseService } from './lakehouse.service';
import { ChangePage, QueryResponse, Snapshot, TableReference, TableRestore } from './models';
import { PanelErrorComponent } from './panel-error.component';
import { ResultGridComponent } from './result-grid.component';

const HISTORY_PREVIEW_ROWS = 500;

/**
 * Catalog snapshot history with table-level drill-down.
 *
 * The engine already owns snapshots, historical reads, and the typed change feed. This panel joins
 * those capabilities without inventing a second history model: snapshot rows drive bounded CDC
 * reads and ordinary `AT (VERSION => n)` SQL through the same API used by the workbench.
 */
@Component({
  selector: 'lh-data-history-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ChangeGridComponent, PanelErrorComponent, ResultGridComponent],
  templateUrl: './data-history-panel.component.html',
  styleUrls: ['./panel-shared.css', './data-history-panel.component.css'],
})
export class DataHistoryPanelComponent {
  private readonly api = inject(LakehouseService);
  private snapshotRequest = 0;
  private detailRequest = 0;
  private restoreRequest = 0;

  readonly tenant = input.required<string | null>();
  readonly catalog = input.required<string | null>();
  readonly tables = input.required<TableReference[]>();
  readonly readOnly = input(false);

  /** Hands a historical preview query to the editor for further filtering. */
  readonly prepareSql = output<string>();
  /** Tells the workbench that its persisted query-run list is now stale. */
  readonly queryExecuted = output<void>();

  protected readonly snapshots = signal<Snapshot[]>([]);
  protected readonly snapshotLimit = signal(100);
  protected readonly loadingSnapshots = signal(false);
  protected readonly selectedTableIndex = signal(0);
  protected readonly compareFrom = signal<number | null>(null);
  protected readonly compareTo = signal<number | null>(null);

  protected readonly changes = signal<ChangePage | null>(null);
  protected readonly historicalRows = signal<QueryResponse | null>(null);
  protected readonly detailTitle = signal('');
  protected readonly detailSql = signal('');
  protected readonly loadingDetail = signal(false);
  protected readonly restorePlan = signal<TableRestore | null>(null);
  protected readonly loadingRestore = signal(false);
  protected readonly restoreNotice = signal<string | null>(null);

  protected readonly error = signal<string | null>(null);
  protected readonly errorTitle = signal('Could not read data history');

  protected readonly formatTime = formatTime;
  protected readonly selectedTable = computed(
    () => this.tables()[this.selectedTableIndex()] ?? null,
  );
  protected readonly tableLabel = computed(() => {
    const table = this.selectedTable();
    return table ? `${table.schemaName}.${table.tableName}` : 'table';
  });
  protected readonly comparisonProblem = computed(() => {
    const from = this.compareFrom();
    const to = this.compareTo();
    if (from === null || to === null) {
      return 'Choose two snapshots.';
    }
    if (from >= to) {
      return 'The baseline must be older than the target snapshot.';
    }
    return null;
  });

  constructor() {
    effect(() => {
      this.tenant();
      this.catalog();
      untracked(() => {
        this.selectedTableIndex.set(0);
        this.reload();
      });
    });
  }

  /** Re-reads the timeline after a catalog switch or committed maintenance operation. */
  reload(): void {
    const request = ++this.snapshotRequest;
    this.snapshots.set([]);
    this.compareFrom.set(null);
    this.compareTo.set(null);
    this.clearDetail();
    this.clearRestore();

    const tenant = this.tenant();
    const catalog = this.catalog();
    if (!tenant || !catalog) {
      this.loadingSnapshots.set(false);
      return;
    }

    this.loadingSnapshots.set(true);
    this.error.set(null);
    this.api.getSnapshots(tenant, catalog, this.snapshotLimit()).subscribe({
      next: (snapshots) => {
        if (request !== this.snapshotRequest) {
          return;
        }
        this.snapshots.set(snapshots);
        this.loadingSnapshots.set(false);
        this.compareTo.set(snapshots[0]?.snapshotId ?? null);
        this.compareFrom.set(snapshots[1]?.snapshotId ?? null);
      },
      error: (err: Error) => {
        if (request !== this.snapshotRequest) {
          return;
        }
        this.snapshots.set([]);
        this.loadingSnapshots.set(false);
        this.fail('Could not list snapshots', err.message);
      },
    });
  }

  protected selectTable(event: Event): void {
    this.selectedTableIndex.set(Number((event.target as HTMLSelectElement).value));
    this.clearDetail();
    this.clearRestore();
  }

  protected changeLimit(event: Event): void {
    this.snapshotLimit.set(Number((event.target as HTMLSelectElement).value));
    this.reload();
  }

  protected setCompareFrom(event: Event): void {
    this.compareFrom.set(Number((event.target as HTMLSelectElement).value));
  }

  protected setCompareTo(event: Event): void {
    this.compareTo.set(Number((event.target as HTMLSelectElement).value));
  }

  /** Whether this commit advanced the catalog schema compared with the previous visible snapshot. */
  protected schemaChanged(index: number): boolean {
    const timeline = this.snapshots();
    return index < timeline.length - 1
      ? timeline[index].schemaVersion !== timeline[index + 1].schemaVersion
      : false;
  }

  /** Reads the selected table exactly as it stood at one snapshot, bounded like every UI result. */
  protected browseSnapshot(snapshot: Snapshot): void {
    const tenant = this.tenant();
    const catalog = this.catalog();
    const table = this.selectedTable();
    if (!tenant || !catalog || !table || this.loadingDetail()) {
      return;
    }

    const sql =
      `SELECT *\nFROM ${quoteTable(table.schemaName, table.tableName)} ` +
      `AT (VERSION => ${snapshot.snapshotId})\nLIMIT ${HISTORY_PREVIEW_ROWS + 1};`;

    const request = ++this.detailRequest;
    this.clearRestore();
    this.loadingDetail.set(true);
    this.error.set(null);
    this.changes.set(null);
    this.historicalRows.set(null);
    this.detailTitle.set(`${this.tableLabel()} at snapshot ${snapshot.snapshotId}`);
    this.detailSql.set(sql);

    this.api.execute(tenant, catalog, sql).subscribe({
      next: (response) => {
        if (request !== this.detailRequest) {
          this.queryExecuted.emit();
          return;
        }
        this.historicalRows.set({
          ...response,
          rows: response.rows.slice(0, HISTORY_PREVIEW_ROWS),
          truncated: response.truncated || response.rows.length > HISTORY_PREVIEW_ROWS,
        });
        this.loadingDetail.set(false);
        this.queryExecuted.emit();
      },
      error: (err: Error) => {
        if (request !== this.detailRequest) {
          this.queryExecuted.emit();
          return;
        }
        this.loadingDetail.set(false);
        this.fail(
          `Could not read ${this.tableLabel()} at snapshot ${snapshot.snapshotId}`,
          err.message,
        );
        this.queryExecuted.emit();
      },
    });
  }

  /** Reads only the row-level changes committed in the selected snapshot. */
  protected inspectSnapshotChanges(snapshot: Snapshot): void {
    this.readChanges(
      snapshot.snapshotId,
      snapshot.snapshotId,
      `${this.tableLabel()} changes in snapshot ${snapshot.snapshotId}`,
    );
  }

  /** Compares two table states by reading the commits after the baseline through the target. */
  protected compareSnapshots(): void {
    const from = this.compareFrom();
    const to = this.compareTo();
    if (from === null || to === null || this.comparisonProblem()) {
      return;
    }

    // The feed is inclusive. State(A) -> State(B) therefore starts at A + 1; including A would show
    // the commit that produced the baseline as if it were part of the difference.
    this.readChanges(
      from + 1,
      to,
      `${this.tableLabel()} changes after snapshot ${from} through ${to}`,
    );
  }

  /**
   * Reads a restore plan without changing data.
   *
   * The engine, not the browser, owns schema reconciliation and transaction safety. The UI only
   * confirms the exact row counts and column treatment the server returned.
   */
  protected planRestore(snapshot: Snapshot): void {
    const tenant = this.tenant();
    const catalog = this.catalog();
    const table = this.selectedTable();
    if (!tenant || !catalog || !table || this.loadingRestore()) {
      return;
    }

    const request = ++this.restoreRequest;
    this.clearDetail();
    this.loadingRestore.set(true);
    this.restorePlan.set(null);
    this.restoreNotice.set(null);
    this.error.set(null);

    this.api
      .restoreTable(
        tenant,
        catalog,
        table.schemaName,
        table.tableName,
        snapshot.snapshotId,
        false,
        null,
      )
      .subscribe({
        next: (plan) => {
          if (request !== this.restoreRequest) {
            return;
          }
          this.restorePlan.set(plan);
          this.loadingRestore.set(false);
        },
        error: (err: Error) => {
          if (request !== this.restoreRequest) {
            return;
          }
          this.loadingRestore.set(false);
          this.fail('Could not plan table restore', err.message);
        },
      });
  }

  /** Applies the reviewed plan atomically; the engine rolls back every partial failure. */
  protected confirmRestore(): void {
    const tenant = this.tenant();
    const catalog = this.catalog();
    const plan = this.restorePlan();
    if (!tenant || !catalog || !plan || this.loadingRestore()) {
      return;
    }

    const request = ++this.restoreRequest;
    this.loadingRestore.set(true);
    this.error.set(null);

    this.api
      .restoreTable(
        tenant,
        catalog,
        plan.schema,
        plan.table,
        plan.snapshotId,
        true,
        plan.currentSnapshotId,
      )
      .subscribe({
        next: (applied) => {
          if (request !== this.restoreRequest) {
            return;
          }
          this.reload();
          this.restoreNotice.set(
            `Restored ${applied.schema}.${applied.table} to ${applied.historicalRowCount} ` +
              `row${applied.historicalRowCount === 1 ? '' : 's'} from snapshot ${applied.snapshotId}.`,
          );
        },
        error: (err: Error) => {
          if (request !== this.restoreRequest) {
            return;
          }
          this.loadingRestore.set(false);
          this.restorePlan.set(null);
          this.fail('Could not restore table', err.message);
        },
      });
  }

  protected cancelRestore(): void {
    this.clearRestore();
  }

  protected openPreviewInEditor(): void {
    const sql = this.detailSql();
    if (sql) {
      this.prepareSql.emit(sql);
    }
  }

  private readChanges(fromSnapshot: number, toSnapshot: number, title: string): void {
    const tenant = this.tenant();
    const catalog = this.catalog();
    const table = this.selectedTable();
    if (!tenant || !catalog || !table || this.loadingDetail()) {
      return;
    }

    const request = ++this.detailRequest;
    this.clearRestore();
    this.loadingDetail.set(true);
    this.error.set(null);
    this.historicalRows.set(null);
    this.changes.set(null);
    this.detailSql.set('');
    this.detailTitle.set(title);

    this.api
      .getChanges(tenant, catalog, table.schemaName, table.tableName, fromSnapshot, toSnapshot)
      .subscribe({
        next: (page) => {
          if (request !== this.detailRequest) {
            return;
          }
          this.changes.set(page);
          this.loadingDetail.set(false);
        },
        error: (err: Error) => {
          if (request !== this.detailRequest) {
            return;
          }
          this.loadingDetail.set(false);
          this.fail('Could not read snapshot changes', err.message);
        },
      });
  }

  private clearDetail(): void {
    this.detailRequest++;
    this.loadingDetail.set(false);
    this.changes.set(null);
    this.historicalRows.set(null);
    this.detailTitle.set('');
    this.detailSql.set('');
    this.error.set(null);
  }

  private clearRestore(): void {
    this.restoreRequest++;
    this.loadingRestore.set(false);
    this.restorePlan.set(null);
    this.restoreNotice.set(null);
  }

  private fail(title: string, message: string): void {
    this.errorTitle.set(title);
    this.error.set(message);
  }
}
