import { HttpClient, HttpErrorResponse, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import {
  AccessContext,
  ApiToken,
  BackupGeneration,
  BrowserSession,
  CatalogStorage,
  ChangePage,
  ColumnDistribution,
  CreateTokenRequest,
  CreatedToken,
  EjectBundle,
  EjectResult,
  MaintenanceOperation,
  MaintenanceResult,
  QueryDiagnostic,
  QueryLanguage,
  QueryLanguageStarter,
  QueryResponse,
  MemberStatus,
  QueryRun,
  RestoreResult,
  SavedQuery,
  ScheduledRun,
  Schema,
  Snapshot,
  Subscription,
  CatalogPlacementValue,
  ResolveStoragePathRequest,
  ResolvedStoragePath,
  SystemSettings,
  SystemStorage,
  TabularImportRequest,
  TabularImportResult,
  TableDetail,
  TableFiles,
  TableProfile,
  TableRestore,
  Tenant,
  TenantMember,
  TokenRole,
  UpdateSystemSettings,
} from './models';

/** Base URL of the API. Overridden at build time for a non-default deployment. */
const API_BASE = '/api';

/**
 * An API failure that keeps its status code.
 *
 * The message alone cannot distinguish "you have no credential" from "that query is invalid", and
 * the workbench has to: a 401 means show the sign-in panel, anything else is an error to report.
 */
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly code: string | null = null,
    readonly canRetryWithTolerantProfile = false,
    readonly diagnostics: QueryDiagnostic[] = [],
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

/** Typed client for the LakeHold API. */
@Injectable({ providedIn: 'root' })
export class LakehouseService {
  private readonly http = inject(HttpClient);

  getAccess(): Observable<AccessContext> {
    return this.http.get<AccessContext>(`${API_BASE}/access`).pipe(catchError(toMessage));
  }

  getBrowserSession(): Observable<BrowserSession> {
    return this.http.get<BrowserSession>('/auth/session').pipe(catchError(toMessage));
  }

  getSystemSettings(): Observable<SystemSettings> {
    return this.http.get<SystemSettings>(`${API_BASE}/system-settings`).pipe(catchError(toMessage));
  }

  saveSystemSettings(settings: UpdateSystemSettings): Observable<SystemSettings> {
    return this.http
      .put<SystemSettings>(`${API_BASE}/system-settings`, settings)
      .pipe(catchError(toMessage));
  }

  /**
   * Reads this node's storage placement. Instance-scoped and read-only: there is no companion save,
   * because the values are bound at API startup and come from the deployment's configuration.
   */
  getSystemStorage(): Observable<SystemStorage> {
    return this.http
      .get<SystemStorage>(`${API_BASE}/system-settings/storage`)
      .pipe(catchError(toMessage));
  }

  /**
   * Asks the server where a catalog's Parquet would go.
   *
   * A POST that creates nothing. URI joining and profile-kind matching stay on the server so the
   * preview cannot disagree with the create that follows it; the browser only displays the answer.
   */
  resolveStoragePath(request: ResolveStoragePathRequest): Observable<ResolvedStoragePath> {
    return this.http
      .post<ResolvedStoragePath>(`${API_BASE}/system-settings/storage/resolve`, request)
      .pipe(catchError(toMessage));
  }

  listTenants(): Observable<Tenant[]> {
    return this.http.get<Tenant[]>(`${API_BASE}/tenants`).pipe(catchError(toMessage));
  }

  getQueryLanguages(): Observable<QueryLanguage[]> {
    return this.http.get<QueryLanguage[]>(`${API_BASE}/query-languages`).pipe(catchError(toMessage));
  }

  getQueryStarter(
    tenant: string,
    catalog: string,
    language: string,
  ): Observable<QueryLanguageStarter> {
    return this.http
      .get<QueryLanguageStarter>(
        this.catalogUrl(
          tenant,
          catalog,
          `query-languages/${encodeURIComponent(language)}/starter`,
        ),
      )
      .pipe(catchError(toMessage));
  }

  execute(
    tenant: string,
    catalog: string,
    source: string,
    language = 'sql',
  ): Observable<QueryResponse> {
    return this.http
      .post<QueryResponse>(this.catalogUrl(tenant, catalog, 'query'), { language, source })
      .pipe(catchError(toMessage));
  }

  importFile(
    tenant: string,
    catalog: string,
    file: File,
    request: TabularImportRequest,
  ): Observable<TabularImportResult> {
    let params = new HttpParams()
      .set('fileName', file.name)
      .set('schema', request.schema)
      .set('table', request.table)
      .set('mode', request.mode);

    if (file.name.toLowerCase().endsWith('.xlsx') && request.worksheet.trim()) {
      params = params.set('worksheet', request.worksheet.trim());
    } else if (request.mode === 'custom') {
      params = params
        .set('delimiter', request.delimiter)
        .set('quote', request.quote)
        .set('escape', request.escape)
        .set('newLine', request.newLine)
        .set('header', request.header)
        .set('sampleSize', request.sampleSize)
        .set('ignoreErrors', request.ignoreErrors)
        .set('storeRejects', request.storeRejects);
    }

    return this.http
      .post<TabularImportResult>(this.catalogUrl(tenant, catalog, 'imports/files'), file, {
        params,
        headers: new HttpHeaders({
          'Content-Type': importContentType(file),
        }),
      })
      .pipe(catchError(toMessage));
  }

  listSavedQueries(tenant: string, catalog: string): Observable<SavedQuery[]> {
    return this.http
      .get<SavedQuery[]>(this.catalogUrl(tenant, catalog, 'saved-queries'))
      .pipe(catchError(toMessage));
  }

  createSavedQuery(
    tenant: string,
    catalog: string,
    body: { name: string; description: string | null; sql: string; language: string },
  ): Observable<SavedQuery> {
    return this.http
      .post<SavedQuery>(this.catalogUrl(tenant, catalog, 'saved-queries'), body)
      .pipe(catchError(toMessage));
  }

  updateSavedQuery(
    tenant: string,
    catalog: string,
    query: Pick<SavedQuery, 'id' | 'revision' | 'name' | 'description' | 'sql' | 'language'>,
  ): Observable<SavedQuery> {
    return this.http
      .put<SavedQuery>(this.catalogUrl(tenant, catalog, `saved-queries/${query.id}`), {
        revision: query.revision,
        name: query.name,
        description: query.description,
        sql: query.sql,
        language: query.language ?? 'sql',
      })
      .pipe(catchError(toMessage));
  }

  deleteSavedQuery(
    tenant: string,
    catalog: string,
    id: number,
    revision: number,
  ): Observable<void> {
    return this.http
      .delete<void>(this.catalogUrl(tenant, catalog, `saved-queries/${id}`), {
        params: { revision },
      })
      .pipe(catchError(toMessage));
  }

  executeSavedQuery(tenant: string, catalog: string, id: number): Observable<QueryResponse> {
    return this.http
      .post<QueryResponse>(this.catalogUrl(tenant, catalog, `saved-queries/${id}/execute`), {})
      .pipe(catchError(toMessage));
  }

  publishSavedQuery(
    tenant: string,
    catalog: string,
    id: number,
    revision: number,
    schema: string,
    viewName: string,
  ): Observable<SavedQuery> {
    return this.http
      .post<SavedQuery>(this.catalogUrl(tenant, catalog, `saved-queries/${id}/publish`), {
        revision,
        schema,
        viewName,
      })
      .pipe(catchError(toMessage));
  }

  unpublishSavedQuery(
    tenant: string,
    catalog: string,
    id: number,
    revision: number,
  ): Observable<SavedQuery> {
    return this.http
      .post<SavedQuery>(
        this.catalogUrl(tenant, catalog, `saved-queries/${id}/unpublish`),
        {},
        { params: { revision } },
      )
      .pipe(catchError(toMessage));
  }

  getSchemas(tenant: string, catalog: string): Observable<Schema[]> {
    return this.http
      .get<Schema[]>(this.catalogUrl(tenant, catalog, 'schemas'))
      .pipe(catchError(toMessage));
  }

  /**
   * Reads the catalog's storage footprint: sizes, file counts, and the flush/compaction advisories.
   *
   * A read, so a read-only credential reaches it — someone who cannot run compaction can still see
   * that compaction is needed.
   */
  getStorage(tenant: string, catalog: string): Observable<CatalogStorage> {
    return this.http
      .get<CatalogStorage>(this.catalogUrl(tenant, catalog, 'storage'))
      .pipe(catchError(toMessage));
  }

  /**
   * Lists one table's Parquet data files, optionally as they stood at a given snapshot.
   *
   * Schema and table travel as query parameters, not path segments: a table name may contain a dot
   * or a slash, and encoding those into the path invites every proxy in between to disagree about
   * what the name was.
   */
  getTableFiles(
    tenant: string,
    catalog: string,
    schema: string,
    table: string,
    snapshot: number | null = null,
  ): Observable<TableFiles> {
    const params: Record<string, string | number> = { schema, table };
    if (snapshot !== null) {
      params['snapshot'] = snapshot;
    }

    return this.http
      .get<TableFiles>(this.catalogUrl(tenant, catalog, 'storage/files'), { params })
      .pipe(catchError(toMessage));
  }

  /** Reads one table or view's schema, physical footprint, and partition specification. */
  getTableDetail(
    tenant: string,
    catalog: string,
    schema: string,
    table: string,
  ): Observable<TableDetail> {
    return this.http
      .get<TableDetail>(this.catalogUrl(tenant, catalog, 'table-detail'), {
        params: { schema, table },
      })
      .pipe(catchError(toMessage));
  }

  /** Computes live logical summary statistics for every column. */
  getTableProfile(
    tenant: string,
    catalog: string,
    schema: string,
    table: string,
    snapshot: number | null = null,
  ): Observable<TableProfile> {
    const params: Record<string, string | number> = { schema, table };
    if (snapshot !== null) {
      params['snapshot'] = snapshot;
    }

    return this.http
      .get<TableProfile>(this.catalogUrl(tenant, catalog, 'table-profile'), { params })
      .pipe(catchError(toMessage));
  }

  /** Computes a bounded distribution for one selected column. */
  getColumnDistribution(
    tenant: string,
    catalog: string,
    schema: string,
    table: string,
    column: string,
    snapshot: number | null = null,
    limit = 20,
  ): Observable<ColumnDistribution> {
    const params: Record<string, string | number> = { schema, table, column, limit };
    if (snapshot !== null) {
      params['snapshot'] = snapshot;
    }

    return this.http
      .get<ColumnDistribution>(this.catalogUrl(tenant, catalog, 'column-distribution'), { params })
      .pipe(catchError(toMessage));
  }

  getSnapshots(tenant: string, catalog: string, limit = 25): Observable<Snapshot[]> {
    return this.http
      .get<Snapshot[]>(this.catalogUrl(tenant, catalog, 'snapshots'), { params: { limit } })
      .pipe(catchError(toMessage));
  }

  /**
   * Plans or atomically applies a table-data restore.
   *
   * The server stages historical rows before deleting anything and inserts them through the current
   * table definition, so defaults and constraints remain authoritative. Apply is false until the
   * operator confirms the returned row and schema plan.
   */
  restoreTable(
    tenant: string,
    catalog: string,
    schema: string,
    table: string,
    snapshotId: number,
    apply = false,
    expectedCurrentSnapshotId: number | null = null,
  ): Observable<TableRestore> {
    return this.http
      .post<TableRestore>(
        this.catalogUrl(tenant, catalog, `snapshots/${snapshotId}/restore-table`),
        { schema, table, apply, expectedCurrentSnapshotId },
      )
      .pipe(catchError(toMessage));
  }

  /**
   * Runs a maintenance operation.
   *
   * `expire` and `cleanup` destroy time-travel history and data files respectively, and the server
   * treats them as dry runs unless `apply` is true. The UI shows the dry-run result first and only
   * commits on explicit confirmation.
   */
  runMaintenance(
    tenant: string,
    catalog: string,
    operation: MaintenanceOperation,
    apply = false,
  ): Observable<MaintenanceResult> {
    return this.http
      .post<MaintenanceResult>(
        this.catalogUrl(tenant, catalog, `maintenance/${operation}`),
        {},
        { params: { apply } },
      )
      .pipe(catchError(toMessage));
  }

  /**
   * Creates a workspace. Instance scope: this is the one operation with no tenant to be scoped to,
   * so it needs the bootstrap credential rather than a tenant's own.
   */
  createTenant(slug: string, displayName: string): Observable<unknown> {
    return this.http.post(`${API_BASE}/tenants`, { slug, displayName }).pipe(catchError(toMessage));
  }

  /**
   * Creates a catalog under a workspace. Instance scope, like the workspace itself.
   *
   * `placement` is omitted for the deployment default, which sends exactly the body this method
   * always sent. An explicit placement adds the fields `CreateCatalogRequest` has always accepted
   * and the browser previously dropped.
   */
  createCatalog(
    tenant: string,
    name: string,
    placement?: CatalogPlacementValue,
  ): Observable<unknown> {
    return this.http
      .post(`${API_BASE}/tenants/${encodeURIComponent(tenant)}/catalogs`, {
        name,
        ...(placement
          ? {
              dataPath: placement.dataPath,
              storageProfile: placement.storageProfile,
              readOnly: placement.readOnly,
            }
          : {}),
      })
      .pipe(catchError(toMessage));
  }

  /**
   * Mints a tenant-scoped token, returned once and never recoverable.
   *
   * The workbench needs this immediately after provisioning: a bootstrap token creates tenants but
   * deliberately cannot read data, so the browser has to trade it for a credential that can.
   */
  createToken(tenant: string, request: CreateTokenRequest): Observable<CreatedToken> {
    return this.http
      .post<CreatedToken>(`${API_BASE}/tenants/${encodeURIComponent(tenant)}/tokens`, request)
      .pipe(catchError(toMessage));
  }

  listTokens(tenant: string): Observable<ApiToken[]> {
    return this.http
      .get<ApiToken[]>(`${API_BASE}/tenants/${encodeURIComponent(tenant)}/tokens`)
      .pipe(catchError(toMessage));
  }

  revokeToken(tenant: string, id: number): Observable<void> {
    return this.http
      .delete<void>(`${API_BASE}/tenants/${encodeURIComponent(tenant)}/tokens/${id}`)
      .pipe(catchError(toMessage));
  }

  listMembers(tenant: string): Observable<TenantMember[]> {
    return this.http
      .get<TenantMember[]>(`${API_BASE}/tenants/${encodeURIComponent(tenant)}/members`)
      .pipe(catchError(toMessage));
  }

  updateMember(
    tenant: string,
    id: number,
    change: { role?: TokenRole; status?: MemberStatus },
  ): Observable<TenantMember> {
    return this.http
      .patch<TenantMember>(
        `${API_BASE}/tenants/${encodeURIComponent(tenant)}/members/${id}`,
        change,
      )
      .pipe(catchError(toMessage));
  }

  removeMember(tenant: string, id: number): Observable<void> {
    return this.http
      .delete<void>(`${API_BASE}/tenants/${encodeURIComponent(tenant)}/members/${id}`)
      .pipe(catchError(toMessage));
  }

  listBackups(tenant: string, catalog: string): Observable<BackupGeneration[]> {
    return this.http
      .get<BackupGeneration[]>(this.catalogUrl(tenant, catalog, 'backups'))
      .pipe(catchError(toMessage));
  }

  /**
   * Rebuilds a catalog from a backup into a new metadata file.
   *
   * `targetMetadataPath` must not already exist: restore never overwrites, and the server refuses
   * rather than the client guarding it. An incomplete generation is refused for the same reason.
   */
  restoreBackup(
    tenant: string,
    catalog: string,
    generation: string | null,
    targetMetadataPath: string,
  ): Observable<RestoreResult> {
    return this.http
      .post<RestoreResult>(this.catalogUrl(tenant, catalog, 'backups/restore'), {
        generation,
        targetMetadataPath,
      })
      .pipe(catchError(toMessage));
  }

  listEjects(tenant: string, catalog: string): Observable<EjectBundle[]> {
    return this.http
      .get<EjectBundle[]>(this.catalogUrl(tenant, catalog, 'ejects'))
      .pipe(catchError(toMessage));
  }

  /** Writes a verified eject bundle. Read-only against the catalog; it commits nothing. */
  eject(tenant: string, catalog: string, includeHistory: boolean): Observable<EjectResult> {
    return this.http
      .post<EjectResult>(this.catalogUrl(tenant, catalog, 'eject'), { includeHistory })
      .pipe(catchError(toMessage));
  }

  /**
   * Reads a table's row-level changes over an inclusive snapshot range.
   *
   * The range is inclusive at *both* ends, so a reader resuming after snapshot L opens the next
   * window at L + 1 rather than at L. `toSnapshot` is optional for the live Changes panel and
   * explicit for the history browser, which needs to inspect one commit or compare a bounded range.
   */
  getChanges(
    tenant: string,
    catalog: string,
    schema: string,
    table: string,
    fromSnapshot: number,
    toSnapshot: number | null = null,
    limit = 200,
  ): Observable<ChangePage> {
    const params: Record<string, string | number> = { schema, table, fromSnapshot, limit };
    if (toSnapshot !== null) {
      params['toSnapshot'] = toSnapshot;
    }

    return this.http
      .get<ChangePage>(this.catalogUrl(tenant, catalog, 'changes'), {
        params,
      })
      .pipe(catchError(toMessage));
  }

  listSubscriptions(tenant: string, catalog: string): Observable<Subscription[]> {
    return this.http
      .get<Subscription[]>(this.catalogUrl(tenant, catalog, 'subscriptions'))
      .pipe(catchError(toMessage));
  }

  /**
   * Creates a webhook subscription to the catalog's change feed.
   *
   * The secret signs every delivery and is write-only — no endpoint returns it afterwards, so the
   * caller keeps its own copy or mints a new subscription.
   */
  createSubscription(
    tenant: string,
    catalog: string,
    body: { endpointUrl: string; secret: string; schema: string; table: string | null },
  ): Observable<Subscription> {
    return this.http
      .post<Subscription>(this.catalogUrl(tenant, catalog, 'subscriptions'), body)
      .pipe(catchError(toMessage));
  }

  deleteSubscription(tenant: string, catalog: string, id: number): Observable<void> {
    return this.http
      .delete<void>(this.catalogUrl(tenant, catalog, `subscriptions/${id}`))
      .pipe(catchError(toMessage));
  }

  /**
   * Recent scheduled maintenance runs.
   *
   * Instance-wide rather than per catalog — the server narrows the rows to what the credential may
   * see — so this answers "did last night's backup run" across every catalog at once.
   */
  getScheduledRuns(): Observable<ScheduledRun[]> {
    return this.http
      .get<ScheduledRun[]>(`${API_BASE}/maintenance/schedule`)
      .pipe(catchError(toMessage));
  }

  getHistory(tenant: string, limit = 30): Observable<QueryRun[]> {
    return this.http
      .get<QueryRun[]>(`${API_BASE}/tenants/${encodeURIComponent(tenant)}/history`, {
        params: { limit },
      })
      .pipe(catchError(toMessage));
  }

  private catalogUrl(tenant: string, catalog: string, suffix: string): string {
    return `${API_BASE}/tenants/${encodeURIComponent(tenant)}/catalogs/${encodeURIComponent(catalog)}/${suffix}`;
  }
}

/**
 * Unwraps the API's error body into a plain `Error`.
 *
 * The engine's own message is the most useful thing an IDE can show — it names the offending
 * token and often suggests a correction — so it is surfaced verbatim rather than replaced with a
 * generic failure string.
 */
function toMessage(response: HttpErrorResponse): Observable<never> {
  if (response.status === 0) {
    return throwError(() => new ApiError('Cannot reach the LakeHold API. Is it running?', 0));
  }

  const body: unknown = response.error;
  if (typeof body === 'string' && body.length > 0) {
    return throwError(() => new ApiError(body, response.status));
  }

  if (body && typeof body === 'object' && 'diagnostics' in body) {
    const diagnostics = (body as { diagnostics?: unknown }).diagnostics;
    if (Array.isArray(diagnostics)) {
      const message = diagnostics
        .map((diagnostic: unknown) => {
          if (!diagnostic || typeof diagnostic !== 'object') {
            return '';
          }
          const item = diagnostic as {
            code?: unknown;
            message?: unknown;
            startLine?: unknown;
            startColumn?: unknown;
          };
          const location =
            typeof item.startLine === 'number' && typeof item.startColumn === 'number'
              ? ` (${item.startLine}:${item.startColumn})`
              : '';
          return `${typeof item.code === 'string' ? `${item.code}: ` : ''}${String(item.message ?? '')}${location}`;
        })
        .filter(Boolean)
        .join('\n');
      if (message) {
        return throwError(
          () =>
            new ApiError(
              message,
              response.status,
              'query_source_invalid',
              false,
              diagnostics as QueryDiagnostic[],
            ),
        );
      }
    }
  }

  // ProblemDetails shape, emitted by AddProblemDetails for unhandled failures.
  if (body && typeof body === 'object' && 'detail' in body) {
    const problem = body as {
      detail: unknown;
      code?: unknown;
      canRetryWithTolerantProfile?: unknown;
    };
    const { detail } = problem;
    if (typeof detail === 'string' && detail.length > 0) {
      return throwError(
        () =>
          new ApiError(
            detail,
            response.status,
            typeof problem.code === 'string' ? problem.code : null,
            problem.canRetryWithTolerantProfile === true,
          ),
      );
    }
  }

  return throwError(
    () =>
      new ApiError(
        response.message || `Request failed with status ${response.status}.`,
        response.status,
      ),
  );
}

function importContentType(file: File): string {
  if (file.name.toLowerCase().endsWith('.xlsx')) {
    return 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';
  }

  return file.type === 'text/csv' ? 'text/csv' : 'application/octet-stream';
}
