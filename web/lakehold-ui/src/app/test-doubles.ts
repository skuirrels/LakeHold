import { Observable, of, throwError } from 'rxjs';
import {
  AccessContext,
  ApiToken,
  BackupGeneration,
  BrowserSession,
  CatalogStorage,
  ChangePage,
  ColumnDistribution,
  CreatedToken,
  EjectBundle,
  EjectResult,
  MaintenanceResult,
  QueryResponse,
  QueryLanguage,
  QueryLanguageStarter,
  QueryRun,
  RestoreResult,
  SavedQuery,
  ScheduledRun,
  Schema,
  Snapshot,
  Subscription,
  SystemSettings,
  TableDetail,
  TableFiles,
  TableProfile,
  TableRestore,
  TableStorage,
  TabularImportResult,
  Tenant,
  TenantMember,
} from './models';

/**
 * A stand-in for `LakehouseService` that answers from memory.
 *
 * Deliberately free of any test-runner import so it type-checks under `tsconfig.app.json` as well as
 * under the spec config. Each method records what it was called with, so a spec can assert on the
 * *arguments* — which is where several of the interesting bugs live, including preserving an
 * awkward schema/table reference without reparsing its display label.
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
  restore: TableRestore = {
    schema: 'main',
    table: 't',
    snapshotId: 0,
    currentSnapshotId: 0,
    currentRowCount: 0,
    historicalRowCount: 0,
    restoredColumns: [],
    currentOnlyColumns: [],
    historicalOnlyColumns: [],
    dryRun: true,
  };
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
  tabularImport: TabularImportResult = {
    fileName: 'customers.csv',
    format: 'csv',
    schema: 'main',
    table: 'customers',
    rowsImported: 2,
    rejectedRows: 0,
    recordedErrors: 0,
    rejectsTruncated: false,
    usedAutomaticFallback: false,
    columns: [
      { name: 'id', dataType: 'BIGINT' },
      { name: 'name', dataType: 'VARCHAR' },
    ],
    rejects: [],
    elapsedMilliseconds: 5,
  };

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

  restoreTable(...args: unknown[]): Observable<TableRestore> {
    return this.answer('restoreTable', args, () => this.restore);
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

  access: AccessContext = {
    mode: 'open',
    role: 'owner',
    readOnly: false,
    systemAdmin: false,
    tenantAdmin: true,
  };
  browserSession: BrowserSession = {
    oidcEnabled: false,
    authenticated: false,
    displayName: null,
    systemAdmin: false,
  };
  systemSettings: SystemSettings = {
    mcpEnabled: true,
    mcpAllowWrites: false,
    mcpMaxRowsPerResult: 200,
    mcpPublicBaseUrl: '',
    mcpRoute: '/mcp',
    version: 1,
    updatedUtc: null,
  };
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
  queryLanguages: QueryLanguage[] = [
    {
      id: 'sql',
      displayName: 'SQL',
      editorLanguage: 'sql',
      starterSource: 'SELECT 1',
      readOnly: false,
      supportsSavedQueries: true,
    },
  ];
  readonly queryStarters = new Map<string, QueryLanguageStarter>();
  savedQueries: SavedQuery[] = [];
  /** What the next maintenance call reports. `dryRun` drives the confirmation affordance. */
  maintenance: MaintenanceResult = {
    operation: 'flush',
    detail: 'done',
    elapsedMilliseconds: 1,
    dryRun: false,
  };
  createdToken: CreatedToken = { id: 1, name: 'workbench', token: 'lkh_new-owner-token' };
  tokens: ApiToken[] = [];

  getAccess(...args: unknown[]): Observable<AccessContext> {
    return this.answer('getAccess', args, () => this.access);
  }

  getBrowserSession(...args: unknown[]): Observable<BrowserSession> {
    return this.answer('getBrowserSession', args, () => this.browserSession);
  }

  getSystemSettings(...args: unknown[]): Observable<SystemSettings> {
    return this.answer('getSystemSettings', args, () => this.systemSettings);
  }

  saveSystemSettings(...args: unknown[]): Observable<SystemSettings> {
    return this.answer('saveSystemSettings', args, () => {
      const request = args[0] as SystemSettings;
      this.systemSettings = {
        ...this.systemSettings,
        ...request,
        version: this.systemSettings.version + 1,
        updatedUtc: new Date().toISOString(),
      };
      return this.systemSettings;
    });
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

  members: TenantMember[] = [];

  listMembers(...args: unknown[]): Observable<TenantMember[]> {
    return this.answer('listMembers', args, () => this.members);
  }

  updateMember(...args: unknown[]): Observable<TenantMember> {
    return this.answer('updateMember', args, () => this.members[0]);
  }

  removeMember(...args: unknown[]): Observable<void> {
    return this.answer('removeMember', args, () => undefined as void);
  }

  listTokens(...args: unknown[]): Observable<ApiToken[]> {
    return this.answer('listTokens', args, () => this.tokens);
  }

  revokeToken(...args: unknown[]): Observable<void> {
    return this.answer('revokeToken', args, () => undefined as void);
  }

  getSchemas(...args: unknown[]): Observable<Schema[]> {
    return this.answer('getSchemas', args, () => this.schemas);
  }

  getHistory(...args: unknown[]): Observable<QueryRun[]> {
    return this.answer('getHistory', args, () => this.history);
  }

  getQueryLanguages(...args: unknown[]): Observable<QueryLanguage[]> {
    return this.answer('getQueryLanguages', args, () => this.queryLanguages);
  }

  getQueryStarter(...args: unknown[]): Observable<QueryLanguageStarter> {
    return this.answer('getQueryStarter', args, () => {
      const language = String(args[2]);
      return this.queryStarters.get(language) ?? {
        source: this.queryLanguages.find((candidate) => candidate.id === language)?.starterSource ?? '',
        schemaFingerprint: 'schema-1',
      };
    });
  }

  execute(...args: unknown[]): Observable<QueryResponse> {
    return this.answer('execute', args, () => this.queryResponse);
  }

  importFile(...args: unknown[]): Observable<TabularImportResult> {
    return this.answer('importFile', args, () => this.tabularImport);
  }

  listSavedQueries(...args: unknown[]): Observable<SavedQuery[]> {
    return this.answer('listSavedQueries', args, () => this.savedQueries);
  }

  createSavedQuery(...args: unknown[]): Observable<SavedQuery> {
    return this.answer('createSavedQuery', args, () => {
      const body = args[2] as { name: string; description: string | null; sql: string };
      return savedQuery({
        id: 1,
        name: body.name,
        description: body.description,
        sql: body.sql,
      });
    });
  }

  updateSavedQuery(...args: unknown[]): Observable<SavedQuery> {
    return this.answer('updateSavedQuery', args, () => {
      const body = args[2] as SavedQuery;
      return { ...body, revision: body.revision + 1 };
    });
  }

  deleteSavedQuery(...args: unknown[]): Observable<void> {
    return this.answer('deleteSavedQuery', args, () => undefined);
  }

  executeSavedQuery(...args: unknown[]): Observable<QueryResponse> {
    return this.answer('executeSavedQuery', args, () => this.queryResponse);
  }

  publishSavedQuery(...args: unknown[]): Observable<SavedQuery> {
    return this.answer('publishSavedQuery', args, () => {
      const id = Number(args[2]);
      const revision = Number(args[3]);
      const existing = this.savedQueries.find((query) => query.id === id) ?? savedQuery({ id });
      return {
        ...existing,
        publishedSchema: String(args[4]),
        publishedViewName: String(args[5]),
        publishedRevision: revision,
        publishedUtc: '2026-07-28T12:00:00Z',
      };
    });
  }

  unpublishSavedQuery(...args: unknown[]): Observable<SavedQuery> {
    return this.answer('unpublishSavedQuery', args, () => {
      const id = Number(args[2]);
      const existing = this.savedQueries.find((query) => query.id === id) ?? savedQuery({ id });
      return {
        ...existing,
        publishedSchema: null,
        publishedViewName: null,
        publishedRevision: null,
        publishedUtc: null,
      };
    });
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

/** A reusable query with stable defaults, so specs only state the behavior-relevant fields. */
export function savedQuery(overrides: Partial<SavedQuery> = {}): SavedQuery {
  return {
    id: 1,
    name: 'Revenue by country',
    description: null,
    sql: 'SELECT country, sum(revenue) FROM events GROUP BY country',
    revision: 1,
    createdUtc: '2026-07-28T10:00:00Z',
    updatedUtc: '2026-07-28T10:00:00Z',
    createdByTokenId: null,
    updatedByTokenId: null,
    publishedSchema: null,
    publishedViewName: null,
    publishedRevision: null,
    publishedUtc: null,
    ...overrides,
  };
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
