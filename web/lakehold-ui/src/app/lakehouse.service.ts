import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import {
  AccessContext,
  BackupGeneration,
  CatalogStorage,
  ChangePage,
  CreatedToken,
  EjectBundle,
  EjectResult,
  MaintenanceOperation,
  MaintenanceResult,
  QueryResponse,
  QueryRun,
  RestoreResult,
  ScheduledRun,
  Schema,
  Snapshot,
  Subscription,
  TableFiles,
  Tenant,
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

  listTenants(): Observable<Tenant[]> {
    return this.http.get<Tenant[]>(`${API_BASE}/tenants`).pipe(catchError(toMessage));
  }

  execute(tenant: string, catalog: string, sql: string): Observable<QueryResponse> {
    return this.http
      .post<QueryResponse>(this.catalogUrl(tenant, catalog, 'query'), { sql })
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

  getSnapshots(tenant: string, catalog: string, limit = 25): Observable<Snapshot[]> {
    return this.http
      .get<Snapshot[]>(this.catalogUrl(tenant, catalog, 'snapshots'), { params: { limit } })
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

  /** Creates a catalog under a workspace. Instance scope, like the workspace itself. */
  createCatalog(tenant: string, name: string): Observable<unknown> {
    return this.http
      .post(`${API_BASE}/tenants/${encodeURIComponent(tenant)}/catalogs`, { name })
      .pipe(catchError(toMessage));
  }

  /**
   * Mints a tenant-scoped token, returned once and never recoverable.
   *
   * The workbench needs this immediately after provisioning: a bootstrap token creates tenants but
   * deliberately cannot read data, so the browser has to trade it for a credential that can.
   */
  createToken(tenant: string, name: string, role: string): Observable<CreatedToken> {
    return this.http
      .post<CreatedToken>(`${API_BASE}/tenants/${encodeURIComponent(tenant)}/tokens`, {
        name,
        role,
      })
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
   * window at L + 1 rather than at L. The upper end is left to the server, which reads to the
   * newest snapshot — the workbench is always asking "what has happened since", never for a
   * bounded historical window.
   */
  getChanges(
    tenant: string,
    catalog: string,
    schema: string,
    table: string,
    fromSnapshot: number,
    limit = 200,
  ): Observable<ChangePage> {
    return this.http
      .get<ChangePage>(this.catalogUrl(tenant, catalog, 'changes'), {
        params: { schema, table, fromSnapshot, limit },
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

  // ProblemDetails shape, emitted by AddProblemDetails for unhandled failures.
  if (body && typeof body === 'object' && 'detail' in body) {
    const { detail } = body as { detail: unknown };
    if (typeof detail === 'string' && detail.length > 0) {
      return throwError(() => new ApiError(detail, response.status));
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
