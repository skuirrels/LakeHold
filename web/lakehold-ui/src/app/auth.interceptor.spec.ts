import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthService } from './auth.service';
import { authInterceptor } from './auth.interceptor';

describe('authInterceptor', () => {
  let auth: AuthService;
  let http: HttpClient;
  let requests: HttpTestingController;

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpClient);
    requests = TestBed.inject(HttpTestingController);
  });

  afterEach(() => requests.verify());

  it('attaches the bearer token to LakeHold API requests', () => {
    auth.setToken('lkh_demo_secret');

    http.get('/api/tenants').subscribe();

    const request = requests.expectOne('/api/tenants');
    expect(request.request.headers.get('Authorization')).toBe('Bearer lkh_demo_secret');
    request.flush([]);
  });

  it('does not send the token to another origin', () => {
    auth.setToken('lkh_demo_secret');

    http.get('https://example.com/health').subscribe();

    const request = requests.expectOne('https://example.com/health');
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush({});
  });

  it('leaves anonymous API requests unchanged', () => {
    http.get('/api/tenants').subscribe();

    const request = requests.expectOne('/api/tenants');
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush([]);
  });
});
