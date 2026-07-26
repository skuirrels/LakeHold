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

  it('turns a network failure into an actionable message', async () => {
    const result = firstValueFrom(service.listTenants());
    http.expectOne('/api/tenants').error(new ProgressEvent('network'));

    await expect(result).rejects.toMatchObject({
      message: 'Cannot reach the Lakehold API. Is it running?',
      status: 0,
    } satisfies Partial<ApiError>);
  });
});
