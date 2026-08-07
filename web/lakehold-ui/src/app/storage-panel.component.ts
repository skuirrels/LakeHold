import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { Subscription } from 'rxjs';
import { formatBytes, formatCount } from './format';
import { LakehouseService } from './lakehouse.service';
import { Catalog, CatalogStorage, StorageKind, TableReference, TableStorage } from './models';
import { PanelErrorComponent } from './panel-error.component';
import { TableDetailComponent } from './table-detail.component';

const KIND_LABELS: Readonly<Record<StorageKind, string>> = {
  Local: 'Filesystem',
  S3: 'Amazon S3 or compatible',
  Gcs: 'Google Cloud Storage',
  Azure: 'Azure Blob Storage or ADLS',
};

/**
 * The physical layer: what each table weighs, how many Parquet files it is spread across, and
 * whether the maintenance buttons above are worth pressing.
 *
 * Selecting a table opens its unified detail, file, and column inspector. See `docs/UI.md` for why
 * physical details read DuckLake's catalog rather than listing the data path.
 */
@Component({
  selector: 'lh-storage-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PanelErrorComponent, TableDetailComponent],
  templateUrl: './storage-panel.component.html',
  styleUrls: ['./panel-shared.css', './storage-panel.component.css'],
})
export class StoragePanelComponent implements OnDestroy {
  private readonly api = inject(LakehouseService);
  private storageRequest?: Subscription;

  readonly tenant = input.required<string | null>();
  readonly catalog = input.required<string | null>();
  /** A table selected outside this panel, normally from the catalog explorer. */
  readonly inspect = input<TableReference | null>(null);

  /**
   * The catalog record behind this panel, for the placement summary.
   *
   * Comes from the tenant listing the workbench already holds rather than a second request: the
   * listing has carried the data path, kind, and profile all along, and the browser was discarding
   * them.
   */
  readonly placement = input<Catalog | null>(null);

  protected readonly storage = signal<CatalogStorage | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly errorTitle = signal('Could not read storage');

  /** The table whose inspector is open. */
  protected readonly selectedTable = signal<TableReference | null>(null);

  protected readonly formatBytes = formatBytes;
  protected readonly formatCount = formatCount;

  protected readonly kindLabel = computed(() => {
    const kind = this.placement()?.storageKind;
    return kind ? (KIND_LABELS[kind] ?? kind) : null;
  });

  /**
   * Whether the failure above is deployment configuration rather than a data problem.
   *
   * A catalog whose profile is missing from this node, or whose profile kind no longer matches its
   * path, fails when the engine attaches it — far from the cause, and phrased as though the catalog
   * were broken. It is not: the catalog is fine and the node's configuration is not. The server's
   * message stays the authority; this only decides how to frame it, so a message that stops
   * mentioning a storage profile degrades to the ordinary banner rather than to a wrong one.
   */
  protected readonly configurationError = computed(() => {
    const message = this.error();
    return !!message && /storage profile/i.test(message);
  });

  constructor() {
    // Reload whenever the panel is pointed at a different catalog. The panel is created and
    // destroyed by the tab switch, so this covers first render too.
    //
    // The work is `untracked` so the effect depends on exactly the two inputs above. Without it,
    // `reload` reading `selectedTable` makes that a dependency too — and then opening a table
    // re-runs the effect, which closes it again. The detail panel simply never appeared.
    effect(() => {
      this.tenant();
      this.catalog();
      untracked(() => {
        this.closeTable();
        // A catalog change does not destroy this panel the way a tab change does, so everything
        // describing the previous catalog has to be dropped by hand. Both of these say something
        // false about the catalog now on screen if they survive: a failure that belonged to another
        // catalog, and a table list that is not this catalog's.
        this.error.set(null);
        this.storage.set(null);
        this.reload();
      });
    });

    // An explorer selection arrives before this panel's footprint request completes. Depending on
    // both values opens it as soon as the matching row exists, without teaching the explorer about
    // storage DTOs or moving table state into the workbench.
    effect(() => {
      const requested = this.inspect();
      if (!requested) {
        return;
      }

      untracked(() => this.selectedTable.set(requested));
    });
  }

  ngOnDestroy(): void {
    this.storageRequest?.unsubscribe();
  }

  /**
   * Re-reads the footprint, and the open file list with it.
   *
   * Public because the workbench calls it after a maintenance operation commits: pressing Compact
   * and watching the file count stay put is the whole reason this panel is worth having, and
   * compaction rewrites the very files an open detail panel is listing.
   */
  reload(): void {
    const tenant = this.tenant();
    const catalog = this.catalog();
    this.storageRequest?.unsubscribe();
    if (!tenant || !catalog) {
      this.storage.set(null);
      this.loading.set(false);
      return;
    }

    this.loading.set(true);
    this.storageRequest = this.api.getStorage(tenant, catalog).subscribe({
      next: (storage) => {
        this.storage.set(storage);
        const selected = this.selectedTable();
        if (selected) {
          const refreshed = storage.tables.find(
              (table) =>
                table.schemaName === selected.schemaName && table.tableName === selected.tableName,
            );
          if (refreshed) {
            this.selectedTable.set(refreshed);
          }
        }
        this.loading.set(false);
      },
      error: (err: Error) => {
        this.errorTitle.set('Could not read storage');
        this.error.set(err.message);
        this.loading.set(false);
      },
    });
  }

  /** Opens the unified table inspector. */
  protected openTable(table: TableStorage): void {
    this.selectedTable.set(table);
  }

  protected closeTable(): void {
    this.selectedTable.set(null);
  }

  /** The advisory a row carries, or null when the table needs nothing. */
  protected advisory(table: TableStorage): string | null {
    if (table.needsFlush && table.needsCompaction) {
      return 'Flush and compact';
    }
    if (table.needsFlush) {
      return 'Flush pending';
    }
    if (table.needsCompaction) {
      return 'Fragmented';
    }
    return null;
  }
}
