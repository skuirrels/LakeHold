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
import { formatBytes, formatCount, formatTime } from './format';
import { LakehouseService } from './lakehouse.service';
import {
  ColumnDistribution,
  ColumnProfile,
  Snapshot,
  TableDetail,
  TableFiles,
  TableProfile,
  TableReference,
} from './models';
import { PanelErrorComponent } from './panel-error.component';

type DetailSection = 'overview' | 'files' | 'columns';

/** Logical, physical, and column-level inspection for one selected table. */
@Component({
  selector: 'lh-table-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PanelErrorComponent],
  templateUrl: './table-detail.component.html',
  styleUrls: ['./panel-shared.css', './table-detail.component.css'],
})
export class TableDetailComponent implements OnDestroy {
  private readonly api = inject(LakehouseService);

  readonly tenant = input.required<string>();
  readonly catalog = input.required<string>();
  readonly table = input.required<TableReference>();

  protected readonly detail = signal<TableDetail | null>(null);
  protected readonly detailLoading = signal(false);
  protected readonly section = signal<DetailSection>('overview');
  protected readonly error = signal<string | null>(null);
  protected readonly errorTitle = signal('Could not inspect table');

  protected readonly snapshots = signal<Snapshot[]>([]);
  protected readonly snapshot = signal<number | null>(null);
  protected readonly files = signal<TableFiles | null>(null);
  protected readonly filesLoading = signal(false);
  protected readonly profile = signal<TableProfile | null>(null);
  protected readonly profileLoading = signal(false);
  protected readonly selectedColumn = signal<ColumnProfile | null>(null);
  protected readonly distribution = signal<ColumnDistribution | null>(null);
  protected readonly distributionLoading = signal(false);

  protected readonly formatBytes = formatBytes;
  protected readonly formatCount = formatCount;
  protected readonly formatTime = formatTime;

  private detailRequest?: Subscription;
  private filesRequest?: Subscription;
  private profileRequest?: Subscription;
  private distributionRequest?: Subscription;
  private snapshotsRequest?: Subscription;

  protected readonly currentPartition = computed(
    () => this.detail()?.partitionSpecs.find((spec) => spec.endSnapshot === null) ?? null,
  );

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
    return list.every((file) => file.dataFile.startsWith(`${directory}/`)) ? directory : null;
  });

  protected readonly distributionMax = computed(() =>
    Math.max(0, ...(this.distribution()?.buckets.map((bucket) => bucket.count) ?? [])),
  );

  constructor() {
    effect(() => {
      this.tenant();
      this.catalog();
      this.table();
      untracked(() => this.reload());
    });
  }

  ngOnDestroy(): void {
    this.cancelRequests();
  }

  protected showSection(section: DetailSection): void {
    this.section.set(section);
    this.error.set(null);
    if (section === 'files' && !this.files()) {
      this.ensureSnapshots();
      this.loadFiles();
    }
    if (section === 'columns' && !this.profile()) {
      if (this.detail()?.kind !== 'VIEW') {
        this.ensureSnapshots();
      }
      this.loadProfile();
    }
  }

  protected selectSnapshot(value: string): void {
    this.snapshot.set(value === '' ? null : Number(value));
    this.files.set(null);
    this.profile.set(null);
    this.selectedColumn.set(null);
    this.distribution.set(null);

    if (this.section() === 'files') {
      this.loadFiles();
    } else if (this.section() === 'columns') {
      this.loadProfile();
    }
  }

  protected profileColumn(column: ColumnProfile): void {
    if (this.selectedColumn()?.name === column.name) {
      this.selectedColumn.set(null);
      this.distribution.set(null);
      this.distributionRequest?.unsubscribe();
      return;
    }

    this.selectedColumn.set(column);
    this.distribution.set(null);
    this.distributionLoading.set(true);
    this.error.set(null);
    this.distributionRequest?.unsubscribe();
    this.distributionRequest = this.api
      .getColumnDistribution(
        this.tenant(),
        this.catalog(),
        this.table().schemaName,
        this.table().tableName,
        column.name,
        this.snapshot(),
      )
      .subscribe({
        next: (distribution) => {
          this.distribution.set(distribution);
          this.distributionLoading.set(false);
        },
        error: (err: Error) => {
          this.errorTitle.set('Could not profile column');
          this.error.set(err.message);
          this.distributionLoading.set(false);
        },
      });
  }

  protected fileName(path: string): string {
    const cut = path.lastIndexOf('/');
    return cut === -1 ? path : path.slice(cut + 1);
  }

  protected partitionKey(transform: string, column: string): string {
    return transform === 'identity' ? column : `${transform}(${column})`;
  }

  protected barWidth(count: number): number {
    const max = this.distributionMax();
    return max === 0 ? 0 : Math.max(2, (count / max) * 100);
  }

  protected nullPercentage(column: ColumnProfile): string {
    return column.rowCount === 0
      ? '0%'
      : `${((column.nullCount / column.rowCount) * 100).toFixed(1)}%`;
  }

  private reload(): void {
    this.cancelRequests();
    this.section.set('overview');
    this.detail.set(null);
    this.files.set(null);
    this.profile.set(null);
    this.selectedColumn.set(null);
    this.distribution.set(null);
    this.snapshots.set([]);
    this.snapshot.set(null);
    this.error.set(null);
    this.detailLoading.set(true);

    const table = this.table();
    this.detailRequest = this.api
      .getTableDetail(this.tenant(), this.catalog(), table.schemaName, table.tableName)
      .subscribe({
        next: (detail) => {
          this.detail.set(detail);
          this.detailLoading.set(false);
        },
        error: (err: Error) => {
          this.errorTitle.set('Could not inspect table');
          this.error.set(err.message);
          this.detailLoading.set(false);
        },
      });
  }

  private ensureSnapshots(): void {
    if (this.snapshots().length > 0 || this.snapshotsRequest) {
      return;
    }

    this.snapshotsRequest = this.api.getSnapshots(this.tenant(), this.catalog()).subscribe({
      next: (snapshots) => {
        this.snapshots.set(snapshots);
        this.snapshotsRequest = undefined;
      },
      // The current profile remains useful when snapshot history is advisory-unavailable.
      error: () => (this.snapshotsRequest = undefined),
    });
  }

  private loadFiles(): void {
    const table = this.table();
    this.filesRequest?.unsubscribe();
    this.filesLoading.set(true);
    this.error.set(null);
    this.filesRequest = this.api
      .getTableFiles(
        this.tenant(),
        this.catalog(),
        table.schemaName,
        table.tableName,
        this.snapshot(),
      )
      .subscribe({
        next: (files) => {
          this.files.set(files);
          this.filesLoading.set(false);
        },
        error: (err: Error) => {
          this.errorTitle.set('Could not list files');
          this.error.set(err.message);
          this.filesLoading.set(false);
        },
      });
  }

  private loadProfile(): void {
    const table = this.table();
    this.profileRequest?.unsubscribe();
    this.profileLoading.set(true);
    this.error.set(null);
    this.profileRequest = this.api
      .getTableProfile(
        this.tenant(),
        this.catalog(),
        table.schemaName,
        table.tableName,
        this.snapshot(),
      )
      .subscribe({
        next: (profile) => {
          this.profile.set(profile);
          this.profileLoading.set(false);
        },
        error: (err: Error) => {
          this.errorTitle.set('Could not profile table');
          this.error.set(err.message);
          this.profileLoading.set(false);
        },
      });
  }

  private cancelRequests(): void {
    this.detailRequest?.unsubscribe();
    this.filesRequest?.unsubscribe();
    this.profileRequest?.unsubscribe();
    this.distributionRequest?.unsubscribe();
    this.snapshotsRequest?.unsubscribe();
    this.detailRequest = undefined;
    this.filesRequest = undefined;
    this.profileRequest = undefined;
    this.distributionRequest = undefined;
    this.snapshotsRequest = undefined;
  }
}
