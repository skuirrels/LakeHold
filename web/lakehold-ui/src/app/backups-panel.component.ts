import {
  ChangeDetectionStrategy,
  Component,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { formatCount, formatTime } from './format';
import { LakehouseService } from './lakehouse.service';
import { BackupGeneration, RestoreResult } from './models';
import { PanelErrorComponent } from './panel-error.component';

/** Backup generations, and the restore that rebuilds one into a new catalog. */
@Component({
  selector: 'lh-backups-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PanelErrorComponent],
  templateUrl: './backups-panel.component.html',
  styleUrls: ['./panel-shared.css', './backups-panel.component.css'],
})
export class BackupsPanelComponent {
  private readonly api = inject(LakehouseService);

  readonly tenant = input.required<string | null>();
  readonly catalog = input.required<string | null>();

  protected readonly backups = signal<BackupGeneration[]>([]);
  /** The generation awaiting a restore target, or null when the form is closed. */
  protected readonly restoreFrom = signal<BackupGeneration | null>(null);
  protected readonly restoreTarget = signal('');
  protected readonly restoring = signal(false);
  protected readonly restoreResult = signal<RestoreResult | null>(null);

  protected readonly error = signal<string | null>(null);
  protected readonly errorTitle = signal('Could not list backups');

  protected readonly formatCount = formatCount;
  protected readonly formatTime = formatTime;

  constructor() {
    // `untracked` so the effect depends on exactly the two inputs above and not on anything
    // the reload happens to read — see storage-panel for the bug that taught us this.
    effect(() => {
      this.tenant();
      this.catalog();
      untracked(() => {
        this.cancelRestore();
        this.restoreResult.set(null);
        this.backups.set([]);
        // A catalog change does not destroy this panel the way a tab change does, so a failure that
        // belonged to the previous catalog has to be cleared by hand or it stands over this one.
        this.error.set(null);
        this.reload();
      });
    });
  }

  /** Re-reads the generation list. Public: the workbench calls it after Backup commits. */
  reload(): void {
    const tenant = this.tenant();
    const catalog = this.catalog();
    if (!tenant || !catalog) {
      this.backups.set([]);
      return;
    }

    this.api.listBackups(tenant, catalog).subscribe({
      next: (backups) => this.backups.set(backups),
      error: (err: Error) => this.fail('Could not list backups', err.message),
    });
  }

  /**
   * Opens the restore form for a generation, proposing a target file name.
   *
   * A bare name is what the proposal should be: the server resolves a relative target against its
   * metadata root, so the rebuilt catalog lands beside the ones it belongs with, and the response
   * reports the absolute path it was actually written to. The field stays editable, and restore
   * refuses to overwrite either way, so a target that already exists is rejected rather than
   * silently clobbering a live catalog.
   */
  protected beginRestore(generation: BackupGeneration): void {
    this.restoreFrom.set(generation);
    this.restoreResult.set(null);
    this.error.set(null);
    this.restoreTarget.set(`${this.catalog() ?? 'catalog'}-restored-${generation.generation}.ducklake`);
  }

  protected cancelRestore(): void {
    this.restoreFrom.set(null);
    this.restoreTarget.set('');
  }

  protected confirmRestore(): void {
    const tenant = this.tenant();
    const catalog = this.catalog();
    const generation = this.restoreFrom();
    const target = this.restoreTarget().trim();
    if (!tenant || !catalog || !generation || !target || this.restoring()) {
      return;
    }

    this.restoring.set(true);
    this.error.set(null);

    this.api.restoreBackup(tenant, catalog, generation.generation, target).subscribe({
      next: (result) => {
        this.restoreResult.set(result);
        this.restoring.set(false);
        this.restoreFrom.set(null);
      },
      error: (err: Error) => {
        // An incomplete generation, or a target that already exists. Both are the caller's to fix,
        // and the server's message names which.
        this.fail('Restore failed', err.message);
        this.restoring.set(false);
      },
    });
  }

  private fail(title: string, message: string): void {
    this.errorTitle.set(title);
    this.error.set(message);
  }
}
