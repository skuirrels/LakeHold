import { TestBed } from '@angular/core/testing';
import { AuthService } from './auth.service';

describe('AuthService', () => {
  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({});
  });

  it('starts with no credential', () => {
    const service = TestBed.inject(AuthService);

    expect(service.token()).toBeNull();
    expect(service.hasToken()).toBe(false);
  });

  it('trims and keeps a token for the browser session', () => {
    const service = TestBed.inject(AuthService);

    service.setToken('  lkh_demo_secret  ');

    expect(service.token()).toBe('lkh_demo_secret');
    expect(service.hasToken()).toBe(true);
    expect(sessionStorage.getItem('lakehold.token')).toBe('lkh_demo_secret');
  });

  it('restores the token when the service is created again', () => {
    sessionStorage.setItem('lakehold.token', 'lkh_demo_stored');
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({});

    const service = TestBed.inject(AuthService);

    expect(service.token()).toBe('lkh_demo_stored');
  });

  it('treats a blank token as sign-out', () => {
    const service = TestBed.inject(AuthService);
    service.setToken('lkh_demo_secret');

    service.setToken('   ');

    expect(service.token()).toBeNull();
    expect(sessionStorage.getItem('lakehold.token')).toBeNull();
  });
});
