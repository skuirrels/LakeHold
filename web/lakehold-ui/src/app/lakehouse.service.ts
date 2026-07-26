import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import {
  CreatedToken,
  MaintenanceOperation,
  MaintenanceResult,
  QueryResponse,
  QueryRun,
  Schema,
  Snapshot,
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

/** Typed client for the Lakehold API. */
@Injectable({ providedIn: 'root' })
export class LakehouseService {
  private readonly http = inject(HttpClient);

  listTenants(): Observable<Tenant[]> {
    return this.http.get<Tenant[]>(`${API_BASE}/tenants`).pipe(catchError(toMessage));
  }

  execute(tenant: string, catalog: string, sql: string): Observable<QueryResponse> {
    return this.http
      .post<QueryResponse>(this.catalogUrl(tenant, catalog, 'query'), { sql })
      .pipe(catchError(toMessage));
  }

  getSchemas(tenant: string, catalog: string): Observable<Schema[]> {
    return this.http.get<Schema[]>(this.catalogUrl(tenant, catalog, 'schemas')).pipe(catchError(toMessage));
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
      .post<CreatedToken>(`${API_BASE}/tenants/${encodeURIComponent(tenant)}/tokens`, { name, role })
      .pipe(catchError(toMessage));
  }

  getHistory(tenant: string, limit = 30): Observable<QueryRun[]> {
    return this.http
      .get<QueryRun[]>(`${API_BASE}/tenants/${encodeURIComponent(tenant)}/history`, { params: { limit } })
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
    return throwError(() => new ApiError('Cannot reach the Lakehold API. Is it running?', 0));
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
    () => new ApiError(response.message || `Request failed with status ${response.status}.`, response.status),
  );
}
