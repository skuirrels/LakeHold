import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { LakehouseService } from './lakehouse.service';
import { StorageKind, StorageProfileSummary, SystemStorage } from './models';

/** A provider's environment keys, shown so an operator can configure without leaving the page. */
interface ProviderHelp {
  readonly kind: StorageKind;
  readonly title: string;
  readonly note: string;
  readonly settings: string;
}

const PROVIDER_HELP: readonly ProviderHelp[] = [
  {
    kind: 'Local',
    title: 'Filesystem',
    note:
      'Needs no profile. Safe for one node, or where every node mounts the same durable filesystem ' +
      'at the same path.',
    settings: [
      'Lakehouse__DataRoot=/mnt/lakehold/data',
      'Lakehouse__BackupRoot=/mnt/lakehold/backups',
      'Lakehouse__EjectRoot=/mnt/lakehold/ejects',
    ].join('\n'),
  },
  {
    kind: 'S3',
    title: 'Amazon S3 and S3-compatible',
    note:
      'Add Endpoint, UseSsl, and UrlStyle for MinIO or another compatible service. Temporary ' +
      'credentials may add SessionToken.',
    settings: [
      'Lakehouse__DataRoot=s3://my-bucket/lakehold',
      'Lakehouse__DefaultStorageProfile=primary',
      'Lakehouse__StorageProfiles__primary__Kind=S3',
      'Lakehouse__StorageProfiles__primary__KeyId=…',
      'Lakehouse__StorageProfiles__primary__Secret=…',
      'Lakehouse__StorageProfiles__primary__Region=eu-west-1',
    ].join('\n'),
  },
  {
    kind: 'Gcs',
    title: 'Google Cloud Storage',
    note: 'KeyId and Secret are interoperability HMAC credentials, not a service-account JSON key.',
    settings: [
      'Lakehouse__DataRoot=gs://my-bucket/lakehold',
      'Lakehouse__DefaultStorageProfile=primary',
      'Lakehouse__StorageProfiles__primary__Kind=Gcs',
      'Lakehouse__StorageProfiles__primary__KeyId=…',
      'Lakehouse__StorageProfiles__primary__Secret=…',
    ].join('\n'),
  },
  {
    kind: 'Azure',
    title: 'Azure Blob Storage and ADLS Gen2',
    note:
      'Use AzureConnectionString, or AzureAccountName with an optional AzureCredentialChain such ' +
      'as workload_identity;managed_identity.',
    settings: [
      'Lakehouse__DataRoot=az://my-container/lakehold',
      'Lakehouse__DefaultStorageProfile=primary',
      'Lakehouse__StorageProfiles__primary__Kind=Azure',
      'Lakehouse__StorageProfiles__primary__AzureConnectionString=…',
    ].join('\n'),
  },
];

const KIND_LABELS: Readonly<Record<StorageKind, string>> = {
  Local: 'Filesystem',
  S3: 'Amazon S3 or compatible',
  Gcs: 'Google Cloud Storage',
  Azure: 'Azure Blob Storage or ADLS',
};

/**
 * Where this node puts Parquet, shown as operational fact rather than as a form.
 *
 * Read-only on purpose, and not a stepping stone to an editable one. Accepting an S3 secret, a GCS
 * HMAC secret, or an Azure connection string here would mean persisting cloud credentials in the
 * control plane; profiles stay in deployment configuration, and a catalog row keeps only the name of
 * one. What this surface is for is answering "where does my data actually go, and can this node
 * reach it?" without reading logs or source.
 */
@Component({
  selector: 'lh-storage-configuration',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './storage-configuration.component.html',
  styleUrl: './storage-configuration.component.css',
})
export class StorageConfigurationComponent implements OnInit {
  private readonly api = inject(LakehouseService);
  private readonly document = inject(DOCUMENT);

  protected readonly storage = signal<SystemStorage | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly copyStatus = signal<string | null>(null);
  protected readonly providerHelp = PROVIDER_HELP;

  /**
   * A default profile naming something this node does not have. The deployment's configuration and
   * this node have drifted, and every catalog created against the default will fail to attach — so
   * it is called out rather than rendered as an ordinary value.
   */
  protected readonly unknownDefaultProfile = computed(() => {
    const current = this.storage();
    const name = current?.defaultStorageProfile;
    return !!name && !current.profiles.some((profile) => profile.name === name);
  });

  protected readonly incomplete = computed(
    () => this.storage()?.profiles.filter((profile) => !profile.credentialsConfigured) ?? [],
  );

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.getSystemStorage().subscribe({
      next: (storage) => {
        this.storage.set(storage);
        this.loading.set(false);
      },
      error: (error: Error) => {
        this.loading.set(false);
        this.error.set(error.message);
      },
    });
  }

  protected kindLabel(kind: StorageKind): string {
    return KIND_LABELS[kind] ?? kind;
  }

  /**
   * How a profile authenticates, in words rather than as a redacted value. A local profile needs
   * nothing, so reporting it as "configured" alongside a bucket credential would be a category
   * error — it is stated as not required.
   */
  protected credentialState(profile: StorageProfileSummary): string {
    if (profile.kind === 'Local') {
      return 'Not required';
    }

    if (!profile.credentialsConfigured) {
      return 'Missing';
    }

    return profile.azureAuthentication === 'connection-string'
      ? 'Configured (connection string)'
      : profile.azureAuthentication === 'credential-chain'
        ? 'Configured (credential chain)'
        : 'Configured';
  }

  protected async copy(settings: string): Promise<void> {
    const clipboard = this.document.defaultView?.navigator.clipboard;
    if (!clipboard) {
      this.copyStatus.set('Automatic copy is unavailable. Select and copy the settings manually.');
      return;
    }

    try {
      await clipboard.writeText(settings);
      this.copyStatus.set('Settings copied.');
    } catch {
      this.copyStatus.set('Automatic copy failed. Select and copy the settings manually.');
    }
  }
}
