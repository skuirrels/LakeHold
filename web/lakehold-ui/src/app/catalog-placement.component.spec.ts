import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CatalogPlacementComponent } from './catalog-placement.component';
import { LakehouseService } from './lakehouse.service';
import { ResolveStoragePathRequest } from './models';
import { FakeLakehouseService } from './test-doubles';

describe('CatalogPlacementComponent', () => {
  let api: FakeLakehouseService;
  let fixture: ComponentFixture<CatalogPlacementComponent>;

  beforeEach(() => {
    api = new FakeLakehouseService();
    api.systemStorage = {
      ...api.systemStorage,
      dataRoot: 's3://company-lake/lakehold',
      defaultStorageProfile: 'primary',
      profiles: [
        {
          name: 'primary',
          kind: 'S3',
          region: 'eu-west-1',
          endpoint: null,
          useSsl: true,
          urlStyle: 'vhost',
          credentialsConfigured: true,
          azureAuthentication: null,
        },
        {
          name: 'archive',
          kind: 'Azure',
          region: null,
          endpoint: null,
          useSsl: true,
          urlStyle: 'vhost',
          credentialsConfigured: true,
          azureAuthentication: 'connection-string',
        },
      ],
    };
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: LakehouseService, useValue: api }],
    });
  });

  async function mount(tenantSlug = 'acme', catalogName = 'analytics'): Promise<void> {
    fixture = TestBed.createComponent(CatalogPlacementComponent);
    fixture.componentRef.setInput('tenantSlug', tenantSlug);
    fixture.componentRef.setInput('catalogName', catalogName);
    await settle();
  }

  /**
   * Waits out the preview debounce, then lets the resulting render finish.
   *
   * A real wait rather than fake timers: this project runs zoneless with no zone.js, so neither
   * `fakeAsync` nor Vitest's fake timers can be used — freezing the clock also freezes the
   * scheduling that `whenStable` waits on, and every test hangs.
   */
  async function settle(): Promise<void> {
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve, 380));
    await fixture.whenStable();
  }

  function text(): string {
    return fixture.nativeElement.textContent as string;
  }

  function lastResolve(): ResolveStoragePathRequest {
    return api.lastArgs('resolveStoragePath')?.[0] as ResolveStoragePathRequest;
  }

  function choose(label: string): void {
    const radios = [...fixture.nativeElement.querySelectorAll('input[type="radio"]')];
    const index = label === 'exact' ? 1 : 0;
    (radios[index] as HTMLInputElement).checked = true;
    radios[index].dispatchEvent(new Event('change'));
  }

  it('previews the server-derived path without sending one', async () => {
    await mount();

    // The browser must not join the URI itself. Sending no path is what makes the server derive it.
    expect(lastResolve().dataPath).toBeNull();
    expect(text()).toContain('/var/lib/lakehold/data/acme/analytics');
  });

  it('contributes nothing to the request while the default is chosen', async () => {
    await mount();

    expect(fixture.componentInstance.value()).toEqual({
      dataPath: null,
      storageProfile: null,
      readOnly: false,
    });
  });

  it('does not ask the server to resolve a name that has not been typed', async () => {
    // Otherwise the panel opens showing a complaint about an empty catalog name.
    await mount('acme', '');

    expect(api.countOf('resolveStoragePath')).toBe(0);
    expect(fixture.nativeElement.querySelector('.error')).toBeNull();
  });

  it('re-resolves when the catalog name changes', async () => {
    await mount();
    const before = api.countOf('resolveStoragePath');

    fixture.componentRef.setInput('catalogName', 'ledger');
    await settle();

    expect(api.countOf('resolveStoragePath')).toBeGreaterThan(before);
    expect(lastResolve().catalogName).toBe('ledger');
  });

  it('debounces typing into a single request', async () => {
    await mount();
    const before = api.countOf('resolveStoragePath');

    for (const name of ['l', 'le', 'led', 'ledg', 'ledge', 'ledger']) {
      fixture.componentRef.setInput('catalogName', name);
      await fixture.whenStable();
    }
    await settle();

    // One request for six keystrokes. Without this the preview endpoint is called per character.
    expect(api.countOf('resolveStoragePath') - before).toBe(1);
    expect(lastResolve().catalogName).toBe('ledger');
  });

  it('seeds the exact path from the derived one rather than an empty box', async () => {
    await mount();
    choose('exact');
    await settle();

    const input = fixture.nativeElement.querySelector('input[type="text"]') as HTMLInputElement;
    expect(input.value).toBe('/var/lib/lakehold/data/acme/analytics');
  });

  it('sends an explicit placement once one is chosen', async () => {
    await mount();
    choose('exact');
    await settle();

    const input = fixture.nativeElement.querySelector('input[type="text"]') as HTMLInputElement;
    input.value = 's3://customer-bucket/lakehold/acme/analytics';
    input.dispatchEvent(new Event('input'));
    await settle();

    expect(fixture.componentInstance.value().dataPath).toBe(
      's3://customer-bucket/lakehold/acme/analytics',
    );
    expect(lastResolve().dataPath).toBe('s3://customer-bucket/lakehold/acme/analytics');
  });

  it('offers only profiles that could serve the resolved scheme', async () => {
    api.resolvedPath = {
      dataPath: 's3://company-lake/lakehold/acme/analytics',
      kind: 'S3',
      storageProfile: 'primary',
      derived: true,
    };
    await mount();
    choose('exact');
    await settle();

    const options = [...fixture.nativeElement.querySelectorAll('option')].map((o) =>
      (o as HTMLOptionElement).textContent?.trim(),
    );
    expect(options.some((o) => o?.startsWith('primary'))).toBe(true);
    expect(options.some((o) => o?.startsWith('archive'))).toBe(false);
  });

  it('warns about a filesystem path and not about a bucket', async () => {
    await mount();
    expect(text()).toContain('safe for one node');

    api.resolvedPath = {
      dataPath: 's3://company-lake/lakehold/acme/analytics',
      kind: 'S3',
      storageProfile: 'primary',
      derived: true,
    };
    fixture.componentRef.setInput('catalogName', 'ledger');
    await settle();

    expect(text()).not.toContain('safe for one node');
  });

  it('shows a placement the server refuses instead of letting the create fail', async () => {
    api.failures.set('resolveStoragePath', 'Storage profile "ghost" is not configured.');
    await mount();

    expect(text()).toContain('Storage profile "ghost" is not configured.');
  });

  it('keeps previewing after the server refuses one placement', async () => {
    // The failure mode this guards is silent and total: an inner error propagating through
    // switchMap terminates the subscription, so a single rejected path would leave the preview
    // frozen for the life of the form while the operator corrects it and sees nothing change.
    api.failures.set('resolveStoragePath', 'Storage profile "ghost" is not configured.');
    await mount();
    expect(text()).toContain('is not configured');

    api.failures.delete('resolveStoragePath');
    fixture.componentRef.setInput('catalogName', 'ledger');
    await settle();

    expect(text()).toContain('/var/lib/lakehold/data/acme/ledger');
    expect(text()).not.toContain('is not configured');
  });

  it('hides itself when the profile inventory cannot be read', async () => {
    // Only an instance credential can read it. A caller without one still creates catalogs in the
    // deployment's default location, which is what happened before this component existed.
    api.failures.set('getSystemStorage', 'Forbidden');
    await mount();

    expect(fixture.nativeElement.querySelector('.placement')).toBeNull();
    expect(fixture.componentInstance.value().dataPath).toBeNull();
  });
});
