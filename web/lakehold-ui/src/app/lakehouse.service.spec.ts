import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { ApiError, LakehouseService } from './lakehouse.service';

describe('LakehouseService', () => {
  let service: LakehouseService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(LakehouseService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads the effective workbench access before loading tenant data', () => {
    service.getAccess().subscribe((access) => {
      expect(access).toEqual({ mode: 'demo', role: 'reader', readOnly: true });
    });

    const request = http.expectOne('/api/access');
    expect(request.request.method).toBe('GET');
    request.flush({ mode: 'demo', role: 'reader', readOnly: true });
  });

  it('encodes tenant and catalog names in query routes', () => {
    service.execute('north wind', 'sales/eu', 'SELECT 1').subscribe();

    const request = http.expectOne('/api/tenants/north%20wind/catalogs/sales%2Feu/query');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ sql: 'SELECT 1' });
    request.flush({
      columns: [],
      rows: [],
      truncated: false,
      elapsedMilliseconds: 1,
      rowsAffected: null,
    });
  });

  it('streams the file body with exact custom CSV reader settings', () => {
    const file = new File(['id;name\r\n1;Alice\r\n'], 'schedules.csv', { type: 'text/csv' });
    service
      .importCsv('north wind', 'sales/eu', file, {
        schema: 'main',
        table: 'predicted_schedules',
        mode: 'custom',
        delimiter: ';',
        quote: '"',
        escape: '',
        newLine: 'crlf',
        header: true,
        sampleSize: -1,
        ignoreErrors: true,
        storeRejects: true,
      })
      .subscribe();

    const request = http.expectOne(
      (candidate) =>
        candidate.url === '/api/tenants/north%20wind/catalogs/sales%2Feu/imports/csv' &&
        candidate.params.get('fileName') === 'schedules.csv',
    );
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toBe(file);
    expect(request.request.headers.get('Content-Type')).toBe('text/csv');
    expect(
      Object.fromEntries(
        request.request.params.keys().map((key) => [key, request.request.params.get(key)]),
      ),
    ).toMatchObject({
      schema: 'main',
      table: 'predicted_schedules',
      mode: 'custom',
      delimiter: ';',
      quote: '"',
      escape: '',
      newLine: 'crlf',
      header: 'true',
      sampleSize: '-1',
      ignoreErrors: 'true',
      storeRejects: 'true',
    });
    request.flush({
      fileName: 'schedules.csv',
      schema: 'main',
      table: 'predicted_schedules',
      rowsImported: 1,
      rejectedRows: 0,
      recordedErrors: 0,
      rejectsTruncated: false,
      columns: [],
      rejects: [],
      elapsedMilliseconds: 1,
    });
  });

  it('uses revisioned catalog routes for saved-query publication', () => {
    service.publishSavedQuery('north wind', 'sales/eu', 17, 3, 'reporting', 'revenue').subscribe();

    const publish = http.expectOne(
      '/api/tenants/north%20wind/catalogs/sales%2Feu/saved-queries/17/publish',
    );
    expect(publish.request.method).toBe('POST');
    expect(publish.request.body).toEqual({
      revision: 3,
      schema: 'reporting',
      viewName: 'revenue',
    });
    publish.flush({
      id: 17,
      name: 'Revenue',
      description: null,
      sql: 'SELECT 1',
      revision: 3,
      createdUtc: '2026-07-28T10:00:00Z',
      updatedUtc: '2026-07-28T10:00:00Z',
      createdByTokenId: null,
      updatedByTokenId: null,
      publishedSchema: 'reporting',
      publishedViewName: 'revenue',
      publishedRevision: 3,
      publishedUtc: '2026-07-28T10:01:00Z',
    });
  });

  it('sends table names as query parameters without path ambiguity', () => {
    service.getTableFiles('demo', 'analytics', 'odd schema', 'orders/2026', 17).subscribe();

    const request = http.expectOne(
      (candidate) =>
        candidate.url === '/api/tenants/demo/catalogs/analytics/storage/files' &&
        candidate.params.get('schema') === 'odd schema' &&
        candidate.params.get('table') === 'orders/2026' &&
        candidate.params.get('snapshot') === '17',
    );
    expect(request.request.method).toBe('GET');
    request.flush({
      schemaName: 'odd schema',
      tableName: 'orders/2026',
      snapshotId: 17,
      truncated: false,
      files: [],
    });
  });

  it('keeps table, column, and snapshot identifiers out of profile paths', () => {
    service
      .getColumnDistribution('demo', 'analytics', 'odd schema', 'orders/2026', 'select', 17, 12)
      .subscribe();

    const request = http.expectOne(
      (candidate) =>
        candidate.url === '/api/tenants/demo/catalogs/analytics/column-distribution' &&
        candidate.params.get('schema') === 'odd schema' &&
        candidate.params.get('table') === 'orders/2026' &&
        candidate.params.get('column') === 'select' &&
        candidate.params.get('snapshot') === '17' &&
        candidate.params.get('limit') === '12',
    );
    expect(request.request.method).toBe('GET');
    request.flush({
      schemaName: 'odd schema',
      tableName: 'orders/2026',
      columnName: 'select',
      dataType: 'BIGINT',
      snapshotId: 17,
      kind: 'range',
      nullCount: 0,
      truncated: false,
      buckets: [],
    });
  });

  it('uses dedicated query-parameter routes for detail and historical table profiles', () => {
    service.getTableDetail('demo', 'analytics', 'odd schema', 'orders/2026').subscribe();
    service.getTableProfile('demo', 'analytics', 'odd schema', 'orders/2026', 17).subscribe();

    const detail = http.expectOne(
      (candidate) =>
        candidate.url === '/api/tenants/demo/catalogs/analytics/table-detail' &&
        candidate.params.get('schema') === 'odd schema' &&
        candidate.params.get('table') === 'orders/2026',
    );
    expect(detail.request.method).toBe('GET');
    detail.flush({
      schemaName: 'odd schema',
      tableName: 'orders/2026',
      kind: 'VIEW',
      columns: [],
      storage: null,
      partitionSpecs: [],
      targetFileSizeBytes: null,
      advisoryFileSizeBytes: 16_000_000,
    });

    const profile = http.expectOne(
      (candidate) =>
        candidate.url === '/api/tenants/demo/catalogs/analytics/table-profile' &&
        candidate.params.get('schema') === 'odd schema' &&
        candidate.params.get('table') === 'orders/2026' &&
        candidate.params.get('snapshot') === '17',
    );
    expect(profile.request.method).toBe('GET');
    profile.flush({
      schemaName: 'odd schema',
      tableName: 'orders/2026',
      snapshotId: 17,
      rowCount: 0,
      columns: [],
    });
  });

  it('keeps destructive maintenance in dry-run mode unless apply is explicit', () => {
    service.runMaintenance('demo', 'analytics', 'cleanup').subscribe();

    const request = http.expectOne(
      (candidate) =>
        candidate.url.endsWith('/maintenance/cleanup') && candidate.params.get('apply') === 'false',
    );
    expect(request.request.method).toBe('POST');
    request.flush({
      operation: 'cleanup',
      detail: 'nothing',
      elapsedMilliseconds: 1,
      dryRun: true,
    });
  });

  it('preserves a problem detail and status code', async () => {
    const result = firstValueFrom(service.listTenants());
    http
      .expectOne('/api/tenants')
      .flush({ detail: 'credential expired' }, { status: 401, statusText: 'Unauthorized' });

    await expect(result).rejects.toMatchObject({
      name: 'ApiError',
      message: 'credential expired',
      status: 401,
    } satisfies Partial<ApiError>);
  });

  it('preserves structured CSV retry guidance without exposing the raw engine error', async () => {
    const result = firstValueFrom(
      service.importCsv(
        'demo',
        'analytics',
        new File(['id;name\r\n1\r\n'], 'customers.csv', { type: 'text/csv' }),
        {
          schema: 'main',
          table: 'customers',
          mode: 'automatic',
          delimiter: ';',
          quote: '"',
          escape: '',
          newLine: 'crlf',
          header: true,
          sampleSize: -1,
          ignoreErrors: true,
          storeRejects: true,
        },
      ),
    );
    http
      .expectOne((request) => request.url.endsWith('/imports/csv'))
      .flush(
        {
          title: 'CSV parsing failed',
          detail: 'CSV line 3 contains 1 column; 2 were expected.',
          code: 'csv_parse_error',
          canRetryWithTolerantProfile: true,
        },
        { status: 400, statusText: 'Bad Request' },
      );

    await expect(result).rejects.toMatchObject({
      name: 'ApiError',
      message: 'CSV line 3 contains 1 column; 2 were expected.',
      status: 400,
      code: 'csv_parse_error',
      canRetryWithTolerantProfile: true,
    } satisfies Partial<ApiError>);
  });

  it('turns a network failure into an actionable message', async () => {
    const result = firstValueFrom(service.listTenants());
    http.expectOne('/api/tenants').error(new ProgressEvent('network'));

    await expect(result).rejects.toMatchObject({
      message: 'Cannot reach the LakeHold API. Is it running?',
      status: 0,
    } satisfies Partial<ApiError>);
  });
});
