import { Observable, of, throwError } from 'rxjs';
import {
  AccessContext,
  BackupGeneration,
  CatalogStorage,
  ChangePage,
  ColumnDistribution,
  CreatedToken,
  EjectBundle,
  EjectResult,
  MaintenanceResult,
  QueryResponse,
  QueryRun,
  RestoreResult,
  ScheduledRun,
  Schema,
  Snapshot,
  Subscription,
  TableDetail,
  TableFiles,
  TableProfile,
  TableStorage,
  Tenant,
} from './models';

/**
 * A stand-in for `LakehouseService` that answers from memory.
 *
 * Deliberately free of any test-runner import so it type-checks under `tsconfig.app.json` as well as
 * under the spec config. Each method records what it was called with, so a spec can assert on the
 * *arguments* — which is where several of the interesting bugs live, the schema-qualified table split
 * being one.
 */
export class FakeLakehouseService {
  readonly calls: { method: string; args: unknown[] }[] = [];

  storage: CatalogStorage = {
    tables: [],
    targetFileSizeBytes: null,
    advisoryFileSizeBytes: 16_000_000,
  };
  files: TableFiles = {
    schemaName: 'main',
    tableName: 't',
    snapshotId: null,
    truncated: false,
    files: [],
  };
  detail: TableDetail = {
    schemaName: 'main',
    tableName: 't',
    kind: 'BASE TABLE',
    columns: [{ name: 'id', dataType: 'BIGINT', isNullable: false }],
    storage: null,
    partitionSpecs: [],
    targetFileSizeBytes: null,
    advisoryFileSizeBytes: 16_000_000,
  };
  profile: TableProfile = {
    schemaName: 'main',
    tableName: 't',
    snapshotId: null,
    rowCount: 0,
    columns: [],
  };
  distribution: ColumnDistribution = {
    schemaName: 'main',
    tableName: 't',
    columnName: 'id',
    dataType: 'BIGINT',
    snapshotId: null,
    kind: 'range',
    nullCount: 0,
    truncated: false,
    buckets: [],
  };
  snapshots: Snapshot[] = [];
  changes: ChangePage = {
    schema: 'main',
    table: 't',
    fromSnapshot: 0,
    toSnapshot: 0,
    truncated: false,
    changes: [],
  };
  subscriptions: Subscription[] = [];
  backups: BackupGeneration[] = [];
  ejects: EjectBundle[] = [];
  scheduledRuns: ScheduledRun[] = [];

  /** Method names that should fail instead of answering, mapped to the message they fail with. */
  readonly failures = new Map<string, string>();

  /** Exact errors for cases where the error type or status is part of the behavior under test. */
  readonly errors = new Map<string, Error>();

  getStorage(...args: unknown[]): Observable<CatalogStorage> {
    return this.answer('getStorage', args, () => this.storage);
  }

  getTableFiles(...args: unknown[]): Observable<TableFiles> {
    return this.answer('getTableFiles', args, () => this.files);
  }

  getTableDetail(...args: unknown[]): Observable<TableDetail> {
    return this.answer('getTableDetail', args, () => this.detail);
  }

  getTableProfile(...args: unknown[]): Observable<TableProfile> {
    return this.answer('getTableProfile', args, () => this.profile);
  }

  getColumnDistribution(...args: unknown[]): Observable<ColumnDistribution> {
    return this.answer('getColumnDistribution', args, () => this.distribution);
  }

  getSnapshots(...args: unknown[]): Observable<Snapshot[]> {
    return this.answer('getSnapshots', args, () => this.snapshots);
  }

  getChanges(...args: unknown[]): Observable<ChangePage> {
    return this.answer('getChanges', args, () => this.changes);
  }

  listSubscriptions(...args: unknown[]): Observable<Subscription[]> {
    return this.answer('listSubscriptions', args, () => this.subscriptions);
  }

  createSubscription(...args: unknown[]): Observable<Subscription> {
    return this.answer('createSubscription', args, () => this.subscriptions[0]);
  }

  deleteSubscription(...args: unknown[]): Observable<void> {
    return this.answer('deleteSubscription', args, () => undefined as void);
  }

  listBackups(...args: unknown[]): Observable<BackupGeneration[]> {
    return this.answer('listBackups', args, () => this.backups);
  }

  restoreBackup(...args: unknown[]): Observable<RestoreResult> {
    return this.answer('restoreBackup', args, () => ({
      metadataPath: 'restored.ducklake',
      generation: String(args[2] ?? ''),
      tablesRestored: 3,
      rowsRestored: 9,
    }));
  }

  listEjects(...args: unknown[]): Observable<EjectBundle[]> {
    return this.answer('listEjects', args, () => this.ejects);
  }

  eject(...args: unknown[]): Observable<EjectResult> {
    return this.answer('eject', args, () => ({
      location: '/bundles/latest',
      tableCount: 2,
      totalRows: 10,
      verified: true,
      digestDeferred: false,
      isSigned: false,
      includesHistory: Boolean(args[2]),
    }));
  }

  getScheduledRuns(...args: unknown[]): Observable<ScheduledRun[]> {
    return this.answer('getScheduledRuns', args, () => this.scheduledRuns);
  }

  // ---- Surfaces the workbench itself uses --------------------------------------------------

  access: AccessContext = { mode: 'open', role: 'owner', readOnly: false };
  tenants: Tenant[] = [
    {
      slug: 'demo',
      displayName: 'Demo workspace',
      catalogs: [{ name: 'analytics', dataPath: '/d', isReadOnly: false }],
    },
  ];
  schemas: Schema[] = [];
  history: QueryRun[] = [];
  queryResponse: QueryResponse = {
    columns: [],
    rows: [],
    truncated: false,
    elapsedMilliseconds: 1,
    rowsAffected: null,
  };
  /** What the next maintenance call reports. `dryRun` drives the confirmation affordance. */
  maintenance: MaintenanceResult = {
    operation: 'flush',
    detail: 'done',
    elapsedMilliseconds: 1,
    dryRun: false,
  };
  createdToken: CreatedToken = { id: 1, name: 'workbench', token: 'lkh_new-owner-token' };

  getAccess(...args: unknown[]): Observable<AccessContext> {
    return this.answer('getAccess', args, () => this.access);
  }

  listTenants(...args: unknown[]): Observable<Tenant[]> {
    return this.answer('listTenants', args, () => this.tenants);
  }

  createTenant(...args: unknown[]): Observable<unknown> {
    return this.answer('createTenant', args, () => ({}));
  }

  createCatalog(...args: unknown[]): Observable<unknown> {
    return this.answer('createCatalog', args, () => ({}));
  }

  createToken(...args: unknown[]): Observable<CreatedToken> {
    return this.answer('createToken', args, () => this.createdToken);
  }

  getSchemas(...args: unknown[]): Observable<Schema[]> {
    return this.answer('getSchemas', args, () => this.schemas);
  }

  getHistory(...args: unknown[]): Observable<QueryRun[]> {
    return this.answer('getHistory', args, () => this.history);
  }

  execute(...args: unknown[]): Observable<QueryResponse> {
    return this.answer('execute', args, () => this.queryResponse);
  }

  runMaintenance(...args: unknown[]): Observable<MaintenanceResult> {
    return this.answer('runMaintenance', args, () => ({
      ...this.maintenance,
      operation: String(args[2] ?? ''),
    }));
  }

  /** The arguments the named method was last called with, or undefined if it never was. */
  lastArgs(method: string): unknown[] | undefined {
    return this.calls.filter((c) => c.method === method).at(-1)?.args;
  }

  countOf(method: string): number {
    return this.calls.filter((c) => c.method === method).length;
  }

  private answer<T>(method: string, args: unknown[], value: () => T): Observable<T> {
    this.calls.push({ method, args });
    const exactError = this.errors.get(method);
    if (exactError) {
      return throwError(() => exactError);
    }

    const failure = this.failures.get(method);
    return failure ? throwError(() => new Error(failure)) : of(value());
  }
}

/** A storage row with sensible defaults, so a spec only states the fields it cares about. */
export function tableStorage(overrides: Partial<TableStorage> = {}): TableStorage {
  return {
    schemaName: 'main',
    tableName: 'events',
    rowCount: 100,
    inlinedRows: 0,
    fileCount: 1,
    fileSizeBytes: 1_000,
    deleteFileCount: 0,
    deleteFileSizeBytes: 0,
    averageFileSizeBytes: 1_000,
    needsFlush: false,
    needsCompaction: false,
    ...overrides,
  };
}
