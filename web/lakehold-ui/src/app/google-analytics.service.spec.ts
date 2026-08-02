import { TestBed } from '@angular/core/testing';
import { NavigationEnd, NavigationStart, Router } from '@angular/router';
import { Subject } from 'rxjs';
import { GoogleAnalyticsService, isWebsitePath } from './google-analytics.service';

class RouterStub {
  readonly events = new Subject<NavigationStart | NavigationEnd>();

  navigate(url: string): void {
    this.events.next(new NavigationStart(1, url));
    this.events.next(new NavigationEnd(1, url, url));
  }
}

describe('GoogleAnalyticsService', () => {
  let router: RouterStub;

  beforeEach(() => {
    router = new RouterStub();
    document.head.querySelector('#lakehold-google-analytics')?.remove();
    Reflect.deleteProperty(window, 'dataLayer');
    Reflect.deleteProperty(window, 'gtag');
    Reflect.deleteProperty(window, 'ga-disable-G-LAKEHOLD1');

    TestBed.configureTestingModule({
      providers: [{ provide: Router, useValue: router }],
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('recognises only the public website routes', () => {
    for (const path of [
      '/',
      '/compare',
      '/docs',
      '/docs/linq-workbench',
      '/provider',
      '/provider/docs',
    ]) {
      expect(isWebsitePath(path), path).toBe(true);
    }

    expect(isWebsitePath('/workbench')).toBe(false);
    expect(isWebsitePath('/api/tenants')).toBe(false);
  });

  it('does not request analytics configuration from the Workbench', () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    TestBed.inject(GoogleAnalyticsService).init();

    router.navigate('/workbench');

    expect(fetchMock).not.toHaveBeenCalled();
    expect(document.head.querySelector('#lakehold-google-analytics')).toBeNull();
  });

  it('loads and records a page view on a public website route', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({ measurementId: 'G-LAKEHOLD1' }),
    });
    vi.stubGlobal('fetch', fetchMock);
    TestBed.inject(GoogleAnalyticsService).init();

    router.navigate('/docs?source=test');

    await vi.waitFor(() => {
      expect(document.head.querySelector('#lakehold-google-analytics')).not.toBeNull();
    });

    expect(fetchMock).toHaveBeenCalledWith('/analytics-config.json', {
      credentials: 'same-origin',
    });
    const script = document.head.querySelector<HTMLScriptElement>('#lakehold-google-analytics');
    expect(script?.src).toBe('https://www.googletagmanager.com/gtag/js?id=G-LAKEHOLD1');

    const commands = ((window as Window & { dataLayer?: IArguments[] }).dataLayer ?? []).map(
      (command) => Array.from(command),
    );
    expect(commands).toContainEqual(['config', 'G-LAKEHOLD1']);
  });

  it('disables collection when a website visitor enters the Workbench', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: true,
        json: async () => ({ measurementId: 'G-LAKEHOLD1' }),
      }),
    );
    TestBed.inject(GoogleAnalyticsService).init();

    router.navigate('/');
    await vi.waitFor(() => {
      expect(document.head.querySelector('#lakehold-google-analytics')).not.toBeNull();
    });
    router.navigate('/workbench');

    expect(Reflect.get(window, 'ga-disable-G-LAKEHOLD1')).toBe(true);
    const configCommands = ((window as Window & { dataLayer?: IArguments[] }).dataLayer ?? [])
      .map((command) => Array.from(command))
      .filter((command) => command[0] === 'config');
    expect(configCommands).toHaveLength(1);

    router.navigate('/docs');
    expect(Reflect.get(window, 'ga-disable-G-LAKEHOLD1')).toBe(false);
  });

  it('stays disabled when the website deployment has no measurement ID', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({ measurementId: '' }),
    });
    vi.stubGlobal('fetch', fetchMock);
    TestBed.inject(GoogleAnalyticsService).init();

    router.navigate('/');

    await vi.waitFor(() => {
      expect(fetchMock).toHaveBeenCalledOnce();
    });
    await Promise.resolve();
    expect(Reflect.get(window, 'gtag')).toBeUndefined();
    expect(document.head.querySelector('#lakehold-google-analytics')).toBeNull();
  });
});
