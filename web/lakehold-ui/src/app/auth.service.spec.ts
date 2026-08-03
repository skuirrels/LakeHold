import { TestBed } from '@angular/core/testing';
import { AuthService } from './auth.service';

describe('AuthService', () => {
  beforeEach(() => {
    sessionStorage.clear();
    localStorage.clear();
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

  it('keeps a token across tab closes only when asked to', () => {
    const service = TestBed.inject(AuthService);
    // Start session-scoped, so the later assertion that sessionStorage is empty means the switch
    // cleared it rather than that nothing was ever written there.
    service.setToken('lkh_demo_secret');
    expect(sessionStorage.getItem('lakehold.token')).toBe('lkh_demo_secret');

    service.setToken('lkh_demo_secret', true);

    expect(service.persistent()).toBe(true);
    expect(localStorage.getItem('lakehold.token')).toBe('lkh_demo_secret');
    // Never in both. A session copy left behind would be read back as session-scoped by a later
    // visit and quietly downgrade the choice.
    expect(sessionStorage.getItem('lakehold.token')).toBeNull();
  });

  it('moves the token out of durable storage when the choice is reversed', () => {
    const service = TestBed.inject(AuthService);
    service.setToken('lkh_demo_secret', true);

    service.setToken('lkh_demo_secret', false);

    expect(service.persistent()).toBe(false);
    expect(sessionStorage.getItem('lakehold.token')).toBe('lkh_demo_secret');
    // The whole point of the session default: a credential the operator stopped wanting kept must
    // not survive the tab because an earlier decision wrote it to localStorage.
    expect(localStorage.getItem('lakehold.token')).toBeNull();
  });

  it('restores a persisted token, and reports that it is persisted', () => {
    localStorage.setItem('lakehold.token', 'lkh_demo_persisted');
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({});

    const service = TestBed.inject(AuthService);

    expect(service.token()).toBe('lkh_demo_persisted');
    expect(service.persistent()).toBe(true);
  });

  it('prefers a persisted token over a stale session one', () => {
    sessionStorage.setItem('lakehold.token', 'lkh_demo_session');
    localStorage.setItem('lakehold.token', 'lkh_demo_persisted');
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({});

    const service = TestBed.inject(AuthService);

    expect(service.token()).toBe('lkh_demo_persisted');
  });

  it('clears both stores on sign-out', () => {
    const service = TestBed.inject(AuthService);
    service.setToken('lkh_demo_secret', true);

    service.clear();

    expect(service.token()).toBeNull();
    expect(service.persistent()).toBe(false);
    expect(localStorage.getItem('lakehold.token')).toBeNull();
    expect(sessionStorage.getItem('lakehold.token')).toBeNull();
  });
});
