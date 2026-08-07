import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { LakehouseService } from './lakehouse.service';
import { StorageProfileSummary } from './models';
import { StorageConfigurationComponent } from './storage-configuration.component';
import { FakeLakehouseService } from './test-doubles';

function profile(overrides: Partial<StorageProfileSummary> = {}): StorageProfileSummary {
  return {
    name: 'primary',
    kind: 'S3',
    region: 'eu-west-1',
    endpoint: null,
    useSsl: true,
    urlStyle: 'vhost',
    credentialsConfigured: true,
    azureAuthentication: null,
    ...overrides,
  };
}

describe('StorageConfigurationComponent', () => {
  let api: FakeLakehouseService;
  let fixture: ComponentFixture<StorageConfigurationComponent>;

  beforeEach(() => {
    api = new FakeLakehouseService();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: LakehouseService, useValue: api }],
    });
  });

  async function mount(): Promise<void> {
    fixture = TestBed.createComponent(StorageConfigurationComponent);
    await fixture.whenStable();
  }

  function text(): string {
    return fixture.nativeElement.textContent as string;
  }

  it('shows the roots and the profile inventory', async () => {
    api.systemStorage = {
      dataRoot: 's3://company-lake/lakehold',
      backupRoot: 's3://company-backups/lakehold',
      ejectRoot: 's3://company-exports/lakehold',
      defaultStorageProfile: 'primary',
      profiles: [profile()],
      requiresRestartToChange: true,
    };

    await mount();

    expect(api.countOf('getSystemStorage')).toBe(1);
    expect(text()).toContain('s3://company-lake/lakehold');
    expect(text()).toContain('s3://company-backups/lakehold');
    expect(text()).toContain('s3://company-exports/lakehold');
    expect(text()).toContain('Amazon S3 or compatible');
    expect(text()).toContain('eu-west-1');
  });

  it('offers no control that would edit the configuration', async () => {
    api.systemStorage = { ...api.systemStorage, profiles: [profile()] };

    await mount();

    // The panel's whole claim is that placement is deployment-owned. An input or a save button here
    // would be the first step towards persisting a cloud credential in the control plane, which is
    // the thing this surface exists not to do.
    expect(fixture.nativeElement.querySelectorAll('input, select, textarea, form').length).toBe(0);
    expect(text()).toContain('restarting the API');

    const buttons = [...fixture.nativeElement.querySelectorAll('button')] as HTMLButtonElement[];
    expect(buttons.every((button) => button.textContent?.trim() === 'Copy')).toBe(true);
  });

  it('reports a missing credential as missing rather than as a redacted value', async () => {
    api.systemStorage = {
      ...api.systemStorage,
      profiles: [profile({ name: 'half', credentialsConfigured: false })],
    };

    await mount();

    expect(text()).toContain('Missing');
    expect(text()).toContain('will fail when it attaches');
  });

  it('names a local profile as needing no credential rather than as configured', async () => {
    // "Configured" beside a bucket credential would read as though a filesystem profile had one.
    api.systemStorage = {
      ...api.systemStorage,
      profiles: [profile({ name: 'on-disk', kind: 'Local', region: null })],
    };

    await mount();

    expect(text()).toContain('Not required');
    expect(text()).toContain('Filesystem');
  });

  it('distinguishes the two Azure modes without disclosing either', async () => {
    api.systemStorage = {
      ...api.systemStorage,
      profiles: [
        profile({ name: 'az-string', kind: 'Azure', azureAuthentication: 'connection-string' }),
        profile({ name: 'az-identity', kind: 'Azure', azureAuthentication: 'credential-chain' }),
      ],
    };

    await mount();

    expect(text()).toContain('Configured (connection string)');
    expect(text()).toContain('Configured (credential chain)');
  });

  it('calls out a default profile this node does not have', async () => {
    // Stale deployment configuration: every catalog created against the default will fail to
    // attach, and the failure surfaces far from this cause.
    api.systemStorage = {
      ...api.systemStorage,
      defaultStorageProfile: 'archive',
      profiles: [profile()],
    };

    await mount();

    expect(text()).toContain('is not configured on this node');
  });

  it('does not warn when the default profile is present', async () => {
    api.systemStorage = {
      ...api.systemStorage,
      defaultStorageProfile: 'primary',
      profiles: [profile()],
    };

    await mount();

    expect(text()).not.toContain('is not configured on this node');
  });

  it('shows a load error and retries successfully', async () => {
    api.failures.set('getSystemStorage', 'Storage configuration is unavailable.');
    await mount();

    expect(text()).toContain('Storage configuration is unavailable.');
    expect(fixture.nativeElement.querySelector('table')).toBeNull();

    api.failures.delete('getSystemStorage');
    api.systemStorage = { ...api.systemStorage, profiles: [profile()] };
    (fixture.nativeElement.querySelector('.retry') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(api.countOf('getSystemStorage')).toBe(2);
    expect(fixture.nativeElement.querySelector('table')).toBeTruthy();
  });

  it('says a deployment with no profiles can use local paths only', async () => {
    await mount();

    expect(text()).toContain('No storage profiles are configured');
    expect(text()).toContain('A remote data path must name its profile explicitly.');
  });
});
