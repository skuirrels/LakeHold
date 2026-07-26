import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { formatBytes, formatCount, formatTime } from './format';
import { LakehouseService } from './lakehouse.service';
import { CatalogStorage, Snapshot, TableFiles, TableStorage } from './models';
import { PanelErrorComponent } from './panel-error.component';

/**
 * The physical layer: what each table weighs, how many Parquet files it is spread across, and
 * whether the maintenance buttons above are worth pressing.
 *
 * Selecting a table opens its file list with an as-of snapshot selector. See `docs/UI.md` for why
 * this reads DuckLake's catalog rather than listing the data path.
 */
@Component({
  selector: 'lh-storage-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PanelErrorComponent],
  templateUrl: './storage-panel.component.html',
  styleUrls: ['./panel-shared.css', './storage-panel.component.css'],
})
export class StoragePanelComponent {
  private readonly api = inject(LakehouseService);

  readonly tenant = input.required<string | null>();
  readonly catalog = input.required<string | null>();

  protected readonly storage = signal<CatalogStorage | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly errorTitle = signal('Could not read storage');

  /** The table whose file list is open, and that list. */
  protected readonly selectedTable = signal<TableStorage | null>(null);
  protected readonly files = signal<TableFiles | null>(null);
  protected readonly filesLoading = signal(false);
  /** Snapshot the file list is read at; null is the current one. */
  protected readonly fileSnapshot = signal<number | null>(null);
  protected readonly snapshots = signal<Snapshot[]>([]);

  protected readonly formatBytes = formatBytes;
  protected readonly formatCount = formatCount;
  protected readonly formatTime = formatTime;

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
    if (!tenant || !catalog) {
      this.storage.set(null);
      return;
    }

    this.loading.set(true);
    this.api.getStorage(tenant, catalog).subscribe({
      next: (storage) => {
        this.storage.set(storage);
        this.loading.set(false);
      },
      error: (err: Error) => {
        this.errorTitle.set('Could not read storage');
        this.error.set(err.message);
        this.loading.set(false);
      },
    });

    if (this.selectedTable()) {
      this.refreshFiles();
    }
  }

  /**
   * Opens a table's file list.
   *
   * Snapshots load alongside it so the as-of selector has something to offer — the panel's
   * differentiated move, and one the DuckDB-family tools cannot make because they have no snapshot
   * to select.
   */
  protected openTable(table: TableStorage): void {
    this.selectedTable.set(table);
    this.fileSnapshot.set(null);
    this.refreshFiles();

    const tenant = this.tenant();
    const catalog = this.catalog();
    if (this.snapshots().length === 0 && tenant && catalog) {
      this.api.getSnapshots(tenant, catalog).subscribe({
        // Advisory: without them the selector offers only "Current", which is still usable.
        next: (snapshots) => this.snapshots.set(snapshots),
        error: () => undefined,
      });
    }
  }

  protected closeTable(): void {
    this.selectedTable.set(null);
    this.files.set(null);
    this.fileSnapshot.set(null);
    this.snapshots.set([]);
  }

  /** Re-reads the file list at a chosen snapshot; the empty value means the current one. */
  protected selectFileSnapshot(value: string): void {
    this.fileSnapshot.set(value === '' ? null : Number(value));
    this.refreshFiles();
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

  /**
   * The file's own name, without the directory.
   *
   * Every file in one table's list sits in the same directory, so repeating it per row is noise that
   * pushes the identifying part off the end. The directory is shown once in the panel header, and
   * the full path stays in each row's tooltip.
   */
  protected fileName(path: string): string {
    const cut = path.lastIndexOf('/');
    return cut === -1 ? path : path.slice(cut + 1);
  }

  /** The directory the listed files share, or null when they somehow do not share one. */
  protected readonly fileRoot = computed(() => {
    const list = this.files()?.files ?? [];
    if (list.length === 0) {
      return null;
    }

    const first = list[0].dataFile;
    const cut = first.lastIndexOf('/');
    if (cut === -1) {
      return null;
    }

    const directory = first.slice(0, cut);
    return list.every((f) => f.dataFile.startsWith(`${directory}/`)) ? directory : null;
  });

  private refreshFiles(): void {
    const tenant = this.tenant();
    const catalog = this.catalog();
    const table = this.selectedTable();
    if (!tenant || !catalog || !table) {
      return;
    }

    this.filesLoading.set(true);
    this.error.set(null);
    this.api
      .getTableFiles(tenant, catalog, table.schemaName, table.tableName, this.fileSnapshot())
      .subscribe({
        next: (files) => {
          this.files.set(files);
          this.filesLoading.set(false);
        },
        error: (err: Error) => {
          // A snapshot predating the table's creation is refused by the engine rather than returning
          // nothing, so this is a message worth showing.
          this.errorTitle.set('Could not list files');
          this.error.set(err.message);
          this.files.set(null);
          this.filesLoading.set(false);
        },
      });
  }
}
