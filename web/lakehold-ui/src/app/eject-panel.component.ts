import {
  ChangeDetectionStrategy,
  Component,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { formatBytes, formatCount, formatTime } from './format';
import { LakehouseService } from './lakehouse.service';
import { EjectBundle, EjectResult } from './models';
import { PanelErrorComponent } from './panel-error.component';

/**
 * Verified eject bundles — the exit path, as a product surface.
 *
 * Its own panel rather than a section under Backups: both write a copy of the catalog, but a backup
 * is how you recover *in place* and an eject is how you *leave*, and burying the differentiated one
 * inside the commodity one would be an odd thing for this product to do.
 */
@Component({
  selector: 'lh-eject-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PanelErrorComponent],
  templateUrl: './eject-panel.component.html',
  styleUrls: ['./panel-shared.css', './eject-panel.component.css'],
})
export class EjectPanelComponent {
  private readonly api = inject(LakehouseService);

  readonly tenant = input.required<string | null>();
  readonly catalog = input.required<string | null>();
  /** Lists existing bundles without allowing a public visitor to export the catalog. */
  readonly readOnly = input(false);

  protected readonly bundles = signal<EjectBundle[]>([]);
  protected readonly includeHistory = signal(false);
  protected readonly ejecting = signal(false);
  protected readonly result = signal<EjectResult | null>(null);
  /** The bundle whose attested table list is expanded. */
  protected readonly openBundle = signal<string | null>(null);

  protected readonly error = signal<string | null>(null);
  protected readonly errorTitle = signal('Could not list eject bundles');

  protected readonly formatBytes = formatBytes;
  protected readonly formatCount = formatCount;
  protected readonly formatTime = formatTime;

  constructor() {
    // `untracked` so the effect depends on exactly the two inputs above and not on anything
    // the reload happens to read — see storage-panel for the bug that taught us this.
    effect(() => {
      this.tenant();
      this.catalog();
      untracked(() => {
        this.result.set(null);
        this.openBundle.set(null);
        this.bundles.set([]);
        // A catalog change does not destroy this panel the way a tab change does, so a failure that
        // belonged to the previous catalog has to be cleared by hand or it stands over this one.
        this.error.set(null);
        this.reload();
      });
    });
  }

  reload(): void {
    const tenant = this.tenant();
    const catalog = this.catalog();
    if (!tenant || !catalog) {
      this.bundles.set([]);
      return;
    }

    this.api.listEjects(tenant, catalog).subscribe({
      next: (bundles) => this.bundles.set(bundles),
      error: (err: Error) => this.fail('Could not list eject bundles', err.message),
    });
  }

  /**
   * Writes a bundle.
   *
   * Non-destructive and read-only against the catalog — it commits nothing and works on a read-only
   * share — so unlike expiry and cleanup it needs no dry run.
   */
  protected runEject(): void {
    const tenant = this.tenant();
    const catalog = this.catalog();
    if (!tenant || !catalog || this.ejecting()) {
      return;
    }

    this.ejecting.set(true);
    this.error.set(null);
    this.result.set(null);

    this.api.eject(tenant, catalog, this.includeHistory()).subscribe({
      next: (result) => {
        this.result.set(result);
        this.ejecting.set(false);
        this.reload();
      },
      error: (err: Error) => {
        this.fail('Eject failed', err.message);
        this.ejecting.set(false);
      },
    });
  }

  protected toggleBundle(bundle: string): void {
    this.openBundle.update((open) => (open === bundle ? null : bundle));
  }

  private fail(title: string, message: string): void {
    this.errorTitle.set(title);
    this.error.set(message);
  }
}
