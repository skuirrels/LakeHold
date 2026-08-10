import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ProviderComponent, latestStableProviderVersion } from './provider.component';

describe('latestStableProviderVersion', () => {
  it('selects the newest stable version without trusting index order', () => {
    expect(
      latestStableProviderVersion({
        versions: ['2.0.0-preview.1', '1.18.0', 'invalid', '1.9.9', '1.17.3'],
      }),
    ).toBe('1.18.0');
  });

  it('rejects malformed version indexes', () => {
    expect(latestStableProviderVersion({ versions: '1.18.0' })).toBeNull();
    expect(latestStableProviderVersion(null)).toBeNull();
  });
});

describe('ProviderComponent', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([])],
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('loads the current stable provider release from NuGet', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({ versions: ['1.17.3', '1.18.0', '1.19.0-preview.1'] }),
    });
    vi.stubGlobal('fetch', fetchMock);

    const fixture = TestBed.createComponent(ProviderComponent);
    await fixture.whenStable();

    await vi.waitFor(() => {
      expect(fixture.nativeElement.querySelector('.stats')?.textContent).toContain('v1.18.0');
    });
    expect(fetchMock).toHaveBeenCalledWith(
      'https://api.nuget.org/v3-flatcontainer/duckdb.efcoreprovider/index.json',
      { credentials: 'omit' },
    );
  });

  it('keeps a non-stale fallback when NuGet is unavailable', async () => {
    const fetchMock = vi.fn().mockRejectedValue(new Error('offline'));
    vi.stubGlobal('fetch', fetchMock);

    const fixture = TestBed.createComponent(ProviderComponent);
    await fixture.whenStable();

    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledOnce());
    expect(fixture.nativeElement.querySelector('.stats')?.textContent).toContain('Latest');
    expect(fixture.nativeElement.textContent).not.toContain('v1.17');
  });
});
