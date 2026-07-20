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

export interface Snapshot {
  snapshotId: number;
  committedAt: string;
  schemaVersion: number;
  commitMessage: string | null;
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
  startedUtc: string;
  elapsedMilliseconds: number;
  rowCount: number;
  succeeded: boolean;
  error: string | null;
}

export type MaintenanceOperation = 'flush' | 'compact' | 'backup' | 'expire' | 'cleanup';
