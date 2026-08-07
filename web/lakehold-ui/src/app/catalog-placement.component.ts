import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { EMPTY, Subject, catchError, debounceTime, switchMap } from 'rxjs';
import { LakehouseService } from './lakehouse.service';
import {
  CatalogPlacementValue,
  ResolvedStoragePath,
  StorageKind,
  StorageProfileSummary,
  SystemStorage,
} from './models';

const KIND_LABELS: Readonly<Record<StorageKind, string>> = {
  Local: 'Filesystem',
  S3: 'Amazon S3 or compatible',
  Gcs: 'Google Cloud Storage',
  Azure: 'Azure Blob Storage or ADLS',
};

/** How long to wait after a keystroke before asking the server to resolve a path. */
const PREVIEW_DEBOUNCE_MS = 300;

/**
 * Chooses where a catalog's Parquet goes, shared by every form that creates one.
 *
 * One component rather than one per form, because the alternative is two copies of the same
 * provider rules drifting apart — and the rule that matters most (which profile can serve which
 * scheme) is the one a copy gets wrong silently.
 *
 * It computes no paths. Both the preview and the final validation come from the server, so the
 * browser never joins a URI or decides whether a profile matches a bucket. What it contributes is
 * the choice: deployment default, or an exact path and profile.
 *
 * Degrades to nothing useful rather than to something wrong. The profile inventory is
 * instance-scoped, so a caller who cannot read it gets the default placement and no advanced
 * option — which is the behaviour every catalog had before this existed.
 */
@Component({
  selector: 'lh-catalog-placement',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './catalog-placement.component.html',
  styleUrl: './catalog-placement.component.css',
})
export class CatalogPlacementComponent implements OnInit {
  private readonly api = inject(LakehouseService);
  private readonly destroyRef = inject(DestroyRef);

  readonly tenantSlug = input('');
  readonly catalogName = input('');

  protected readonly storage = signal<SystemStorage | null>(null);
  protected readonly unavailable = signal(false);
  protected readonly mode = signal<'default' | 'exact'>('default');
  protected readonly dataPath = signal('');
  protected readonly storageProfile = signal('');
  protected readonly readOnly = signal(false);
  protected readonly preview = signal<ResolvedStoragePath | null>(null);
  protected readonly previewError = signal<string | null>(null);
  protected readonly resolving = signal(false);

  /** Asking for a preview; kept separate from the effect so the request can be debounced. */
  private readonly requested = new Subject<void>();

  /**
   * What the host should send. Read at submit time rather than pushed through an output, so there
   * is no window in which the parent holds a stale copy of the form.
   */
  readonly value = computed<CatalogPlacementValue>(() =>
    this.mode() === 'exact'
      ? {
          dataPath: this.dataPath().trim() || null,
          storageProfile: this.storageProfile().trim() || null,
          readOnly: this.readOnly(),
        }
      : { dataPath: null, storageProfile: null, readOnly: false },
  );

  /** Profiles that could serve the chosen scheme. The server checks this again on every request. */
  protected readonly selectableProfiles = computed<StorageProfileSummary[]>(() => {
    const kind = this.preview()?.kind;
    const profiles = this.storage()?.profiles ?? [];
    return kind ? profiles.filter((profile) => profile.kind === kind) : profiles;
  });

  protected readonly filesystem = computed(() => this.preview()?.kind === 'Local');

  constructor() {
    // `untracked` so this depends on exactly the five signals named here and not on whatever the
    // request happens to read — the same rule the other panels follow, for the same reason.
    effect(() => {
      this.tenantSlug();
      this.catalogName();
      this.mode();
      this.dataPath();
      this.storageProfile();
      untracked(() => this.requestPreview());
    });

    this.requested
      .pipe(
        debounceTime(PREVIEW_DEBOUNCE_MS),
        switchMap(() => {
          const value = this.value();
          this.resolving.set(true);
          return this.api
            .resolveStoragePath({
              tenantSlug: this.tenantSlug().trim(),
              catalogName: this.catalogName().trim(),
              dataPath: value.dataPath,
              storageProfile: value.storageProfile,
            })
            .pipe(
              // Caught *inside* the inner observable on purpose. An error allowed to reach the
              // outer stream terminates this subscription, and the preview would then be frozen for
              // the life of the form: the operator corrects the path and nothing ever updates
              // again. A refusal has to leave the stream able to answer the next keystroke.
              catchError((error: Error) => {
                // Shown rather than swallowed — the alternative is a create that fails after the
                // tenant already exists.
                this.preview.set(null);
                this.previewError.set(error.message);
                this.resolving.set(false);
                return EMPTY;
              }),
            );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((resolved) => {
        this.preview.set(resolved);
        this.previewError.set(null);
        this.resolving.set(false);
      });
  }

  ngOnInit(): void {
    this.api.getSystemStorage().subscribe({
      next: (storage) => {
        this.storage.set(storage);
        this.unavailable.set(false);
      },
      // Not an error worth showing. Only an instance credential can read the inventory, and a
      // caller without one can still create a catalog in the deployment's default location.
      error: () => this.unavailable.set(true),
    });
  }

  /**
   * Asks for a preview, unless there is nothing to preview yet.
   *
   * The blank check is not an optimisation. A first-run form mounts with an empty catalog name, and
   * without it the panel would open showing the server's complaint about a name the operator has
   * not had a chance to type.
   */
  private requestPreview(): void {
    if (!this.tenantSlug().trim() || !this.catalogName().trim()) {
      this.preview.set(null);
      this.previewError.set(null);
      return;
    }

    this.requested.next();
  }

  protected kindLabel(kind: StorageKind): string {
    return KIND_LABELS[kind] ?? kind;
  }

  protected chooseMode(mode: 'default' | 'exact'): void {
    this.mode.set(mode);
    if (mode === 'exact' && !this.dataPath().trim()) {
      // Start from the derived path rather than an empty box: editing a real path is a smaller ask
      // than composing a URI, and it shows the shape the deployment expects.
      this.dataPath.set(this.preview()?.dataPath ?? '');
      this.storageProfile.set(this.preview()?.storageProfile ?? '');
    }
  }
}
