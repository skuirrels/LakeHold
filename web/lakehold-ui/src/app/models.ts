/** Wire contracts, mirroring Lakehold.Api.Contracts. */

export interface Catalog {
  name: string;
  dataPath: string;
  isReadOnly: boolean;
}

export interface Tenant {
  slug: string;
  displayName: string;
  catalogs: Catalog[];
}

/** Effective permissions for the current workbench visitor. */
export interface AccessContext {
  mode: 'open' | 'authenticated' | 'demo';
  role: 'owner' | 'editor' | 'reader';
  readOnly: boolean;
  systemAdmin: boolean;
}

/** Browser OIDC availability and the current same-origin session. */
export interface BrowserSession {
  oidcEnabled: boolean;
  authenticated: boolean;
  displayName: string | null;
  systemAdmin: boolean;
  /** Whether the node refuses credential-less requests. False means a token is optional here. */
  requiresAuthentication: boolean;
}

/** Instance-wide MCP settings, versioned for optimistic saves. */
export interface SystemSettings {
  mcpEnabled: boolean;
  mcpAllowWrites: boolean;
  mcpMaxRowsPerResult: number;
  mcpPublicBaseUrl: string;
  mcpRoute: string;
  version: number;
  updatedUtc: string | null;
}

export interface UpdateSystemSettings {
  mcpEnabled: boolean;
  mcpAllowWrites: boolean;
  mcpMaxRowsPerResult: number;
  mcpPublicBaseUrl: string;
  version: number;
}

export type TokenRole = 'reader' | 'editor' | 'owner';

/** Complete request accepted by the public tenant-token endpoint. */
export interface CreateTokenRequest {
  name: string;
  readOnly: boolean;
  catalogName: string | null;
  expiresUtc: string | null;
  role: TokenRole;
}

/** Revocable token metadata. The plaintext secret is deliberately absent. */
export interface ApiToken {
  id: number;
  name: string;
  scope: string;
  role: string;
  catalogName: string | null;
  readOnly: boolean;
  createdUtc: string;
  expiresUtc: string | null;
  revokedUtc: string | null;
  lastUsedUtc: string | null;
}

export interface Column {
  name: string;
  /** DuckDB type name, e.g. `BigInt`, `Varchar`, `Struct`. */
  dataType: string;
  /** CLR type the provider materialises, e.g. `Int64`, `String`, `BigInteger`. */
  clrType: string;
}

/**
 * A row's values, aligned to `columns` by ordinal.
 *
 * Wide integers and decimals arrive as strings: JSON numbers are IEEE-754 doubles, so a BIGINT
 * beyond 2^53 would be silently rounded by the browser's parser. The server stringifies them and
 * the grid renders them verbatim.
 */
export type Row = (string | number | boolean | null | unknown)[];

export interface QueryResponse {
  columns: Column[];
  rows: Row[];
  truncated: boolean;
  elapsedMilliseconds: number;
  /**
   * Rows changed by an `INSERT`, `UPDATE`, `DELETE`, or `MERGE`; null for anything else. Null and
   * zero differ: null is "this statement does not report a count", zero is a DML statement that
   * matched nothing.
   */
  rowsAffected: number | null;
  language?: string;
  generatedSql?: string | null;
  diagnostics?: QueryDiagnostic[];
}

export interface QueryDiagnostic {
  severity: string;
  code: string;
  message: string;
  startLine: number;
  startColumn: number;
  endLine: number;
  endColumn: number;
}

export interface QueryLanguage {
  id: string;
  displayName: string;
  editorLanguage: string;
  starterSource: string;
  readOnly: boolean;
  supportsSavedQueries: boolean;
  /** False only for a preserved source whose optional planner is currently unavailable. */
  available?: boolean;
}

export interface QueryLanguageStarter {
  source: string;
  schemaFingerprint: string;
}

export type TabularImportMode = 'automatic' | 'custom';
export type CsvNewLine = 'lf' | 'cr' | 'crlf';

/** Browser-selected tabular-file reader settings. CSV automatic mode omits DuckDB overrides. */
export interface TabularImportRequest {
  schema: string;
  table: string;
  mode: TabularImportMode;
  worksheet: string;
  delimiter: string;
  quote: string;
  escape: string;
  newLine: CsvNewLine;
  header: boolean;
  sampleSize: number;
  ignoreErrors: boolean;
  storeRejects: boolean;
}

export interface TabularImportedColumn {
  name: string;
  dataType: string;
}

export interface CsvReject {
  line: number;
  columnName: string | null;
  errorType: string;
  csvLine: string;
  errorMessage: string;
}

/** Created table and bounded CSV reject report returned after a browser file upload. */
export interface TabularImportResult {
  fileName: string;
  format: 'csv' | 'xlsx';
  schema: string;
  table: string;
  rowsImported: number;
  rejectedRows: number;
  recordedErrors: number;
  rejectsTruncated: boolean;
  usedAutomaticFallback: boolean;
  columns: TabularImportedColumn[];
  rejects: CsvReject[];
  elapsedMilliseconds: number;
}

/** A reusable query authored in one catalog and optionally published as a view. */
export interface SavedQuery {
  id: number;
  name: string;
  description: string | null;
  sql: string;
  language?: string;
  /** Optimistic authoring revision. */
  revision: number;
  createdUtc: string;
  updatedUtc: string;
  createdByTokenId: number | null;
  updatedByTokenId: number | null;
  publishedSchema: string | null;
  publishedViewName: string | null;
  publishedSchemaFingerprint?: string | null;
  /** True when the published LINQ view was compiled against an older catalog shape. */
  publishedSchemaDrifted?: boolean;
  /** Revision currently exposed by the view; lower than `revision` means republish is needed. */
  publishedRevision: number | null;
  publishedUtc: string | null;
}

export interface SchemaColumn {
  name: string;
  dataType: string;
  isNullable: boolean;
}

export interface SchemaTable {
  name: string;
  kind: string;
  columns: SchemaColumn[];
}

export interface Schema {
  name: string;
  tables: SchemaTable[];
}

export interface TableReference {
  schemaName: string;
  tableName: string;
}

export interface Snapshot {
  snapshotId: number;
  committedAt: string;
  schemaVersion: number;
  commitMessage: string | null;
}

/** A read-only plan or committed table-data restore. */
export interface TableRestore {
  schema: string;
  table: string;
  snapshotId: number;
  /** Live snapshot against which the plan was reviewed; apply refuses if the catalog advances. */
  currentSnapshotId: number;
  currentRowCount: number;
  historicalRowCount: number;
  restoredColumns: string[];
  /** Current columns absent from the snapshot; their current defaults/nullability apply. */
  currentOnlyColumns: string[];
  /** Snapshot columns absent from the current table; these are deliberately ignored. */
  historicalOnlyColumns: string[];
  dryRun: boolean;
}

/** One table's physical footprint in the storage view. */
export interface TableStorage {
  schemaName: string;
  tableName: string;
  /** Live rows, as `count(*)` would report: deletes subtracted, inlined rows included. */
  rowCount: number;
  /**
   * Rows committed but not yet written to Parquet. The only thing distinguishing a table whose data
   * is entirely inlined from an empty one — both report zero files.
   */
  inlinedRows: number;
  fileCount: number;
  fileSizeBytes: number;
  deleteFileCount: number;
  deleteFileSizeBytes: number;
  /** Mean bytes per data file, or null when there are no files. */
  averageFileSizeBytes: number | null;
  /** Whether `flush` has work to do. Advisory. */
  needsFlush: boolean;
  /** Whether the table has drifted into the small-file problem. Advisory. */
  needsCompaction: boolean;
}

/** A catalog's storage footprint. */
export interface CatalogStorage {
  tables: TableStorage[];
  /** The catalog's own `target_file_size`, or null when it has never been set. */
  targetFileSizeBytes: number | null;
  /** The threshold `needsCompaction` was computed against, so the advice shows its basis. */
  advisoryFileSizeBytes: number;
}

/** One Parquet data file in the table-detail panel. */
export interface DataFile {
  dataFile: string;
  dataFileSizeBytes: number;
  /** The merge-on-read delete file paired to this data file, or null when it has none. */
  deleteFile: string | null;
  deleteFileSizeBytes: number | null;
}

/** A table's data files at one snapshot. */
export interface TableFiles {
  schemaName: string;
  tableName: string;
  /** The snapshot read, or null for the current one. */
  snapshotId: number | null;
  truncated: boolean;
  files: DataFile[];
}

export interface TableDetailColumn {
  name: string;
  dataType: string;
  isNullable: boolean;
}

export interface PartitionKey {
  position: number;
  columnName: string;
  transform: string;
}

export interface PartitionSpec {
  partitionId: number;
  beginSnapshot: number;
  endSnapshot: number | null;
  keys: PartitionKey[];
}

/** One table or view's logical, physical, and partition detail. */
export interface TableDetail {
  schemaName: string;
  tableName: string;
  kind: string;
  columns: TableDetailColumn[];
  /** Null for views, which own no Parquet files. */
  storage: TableStorage | null;
  partitionSpecs: PartitionSpec[];
  targetFileSizeBytes: number | null;
  advisoryFileSizeBytes: number;
}

export interface ColumnProfile {
  name: string;
  dataType: string;
  rowCount: number;
  nullCount: number;
  minimum: string | null;
  maximum: string | null;
  approxDistinct: string | null;
  mean: string | null;
  standardDeviation: string | null;
  firstQuartile: string | null;
  median: string | null;
  thirdQuartile: string | null;
}

export interface TableProfile {
  schemaName: string;
  tableName: string;
  snapshotId: number | null;
  rowCount: number;
  columns: ColumnProfile[];
}

export interface DistributionBucket {
  label: string;
  lowerBound: string | null;
  upperBound: string | null;
  count: number;
}

export interface ColumnDistribution {
  schemaName: string;
  tableName: string;
  columnName: string;
  dataType: string;
  snapshotId: number | null;
  kind: 'range' | 'categorical' | 'unsupported';
  nullCount: number;
  truncated: boolean;
  buckets: DistributionBucket[];
}

export interface MaintenanceResult {
  operation: string;
  detail: string;
  elapsedMilliseconds: number;
  /** True when the operation only reported what it would do, and changed nothing. */
  dryRun: boolean;
}

export interface QueryRun {
  id: number;
  catalogName: string;
  sql: string;
  language?: string;
  startedUtc: string;
  elapsedMilliseconds: number;
  rowCount: number;
  succeeded: boolean;
  error: string | null;
  /** The credential that ran the statement, or null for pre-auth history. */
  tokenId: number | null;
  /** That credential's label when it still exists; null when anonymous or since deleted. */
  tokenName: string | null;
}

export type MaintenanceOperation = 'flush' | 'compact' | 'backup' | 'expire' | 'cleanup';

/**
 * A freshly minted token.
 *
 * `token` is the plaintext, and the server stores only a hash of it — so this is the one and only
 * time it exists anywhere the user can copy it from.
 */
export interface CreatedToken {
  id: number;
  name: string;
  token: string;
}

/** A backup generation available to restore. */
export interface BackupGeneration {
  generation: string;
  createdUtc: string | null;
  snapshotId: number | null;
  tableCount: number;
  /**
   * False when the generation has no manifest — it died partway through, and restoring it could
   * silently reinstate deleted rows. The API refuses to restore one.
   */
  complete: boolean;
}

/** Outcome of a restore. */
export interface RestoreResult {
  metadataPath: string;
  generation: string;
  tablesRestored: number;
  rowsRestored: number;
}

/** An attested table inside an eject bundle. */
export interface EjectedTable {
  schema: string;
  table: string;
  rowCount: number;
  sha256: string | null;
  bytes: number | null;
}

/** An eject bundle on disk. */
export interface EjectBundle {
  bundle: string;
  createdUtc: string | null;
  snapshotId: number | null;
  includesHistory: boolean;
  isSigned: boolean;
  /** False when the bundle has no manifest — it died partway and is untrusted. */
  complete: boolean;
  tables: EjectedTable[];
}

/** Outcome of an eject. */
export interface EjectResult {
  location: string;
  tableCount: number;
  totalRows: number;
  verified: boolean;
  /** True when per-file digests were skipped because the bundle is on an object store. */
  digestDeferred: boolean;
  isSigned: boolean;
  includesHistory: boolean;
}

/** One row-level change from the CDC feed. */
export interface Change {
  snapshotId: number;
  rowId: number;
  /** `insert`, `delete`, `update_preimage`, or `update_postimage`. */
  changeType: string;
  row: Record<string, unknown>;
}

/** A page of row-level changes. */
export interface ChangePage {
  schema: string;
  table: string;
  fromSnapshot: number;
  toSnapshot: number;
  truncated: boolean;
  changes: Change[];
}

/**
 * A change subscription.
 *
 * Carries no signing secret: it is write-only and no endpoint returns it after creation. Delivery
 * state is included because a subscription you cannot observe is one you do not trust.
 */
export interface Subscription {
  id: number;
  catalog: string;
  schema: string;
  table: string | null;
  endpointUrl: string;
  active: boolean;
  lastDeliveredSnapshot: number;
  consecutiveFailures: number;
  lastAttemptUtc: string | null;
  lastError: string | null;
  createdUtc: string;
}

/** A recent scheduled maintenance run, across every tenant the credential may see. */
export interface ScheduledRun {
  job: string;
  tenant: string;
  catalog: string;
  startedUtc: string;
  elapsedMilliseconds: number;
  succeeded: boolean;
  detail: string;
}
