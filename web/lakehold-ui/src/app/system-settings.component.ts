import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  inject,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { LakehouseService } from './lakehouse.service';
import { SystemSettings, Tenant } from './models';
import { CatalogAdministrationComponent } from './catalog-administration.component';
import { StorageConfigurationComponent } from './storage-configuration.component';
import { WorkspaceAdministrationComponent } from './workspace-administration.component';

const MCP_PUBLIC_BASE_URL_MAX_LENGTH = 2048;

/**
 * Instance administration: independent MCP, storage, workspace, and catalog panels.
 *
 * Everything here answers to `Capability.Instance`. A failure in one panel must not hide the
 * others: provisioning a workspace is unrelated to reading or saving the live MCP settings.
 * Users and tokens are workspace administration and live under their own destination, so an owner
 * is never sent to a page whose contents their credential cannot read.
 */
@Component({
  selector: 'lh-system-settings',
  imports: [
    CatalogAdministrationComponent,
    StorageConfigurationComponent,
    WorkspaceAdministrationComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './system-settings.component.html',
  styleUrls: ['./admin-page.css', './system-settings.component.css'],
})
export class SystemSettingsComponent implements OnInit {
  /** Re-emitted for the workbench, whose workspace and catalog pickers this invalidates. */
  readonly resourcesChanged = output<void>();

  private readonly api = inject(LakehouseService);
  private readonly document = inject(DOCUMENT);
  private readonly catalogAdministration = viewChild(CatalogAdministrationComponent);

  protected readonly settings = signal<SystemSettings | null>(null);
  protected readonly enabled = signal(false);
  protected readonly allowWrites = signal(false);
  protected readonly maxRows = signal(200);
  protected readonly publicBaseUrl = signal('');
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly notice = signal<string | null>(null);
  protected readonly publicBaseUrlMaxLength = MCP_PUBLIC_BASE_URL_MAX_LENGTH;
  protected readonly browserBaseUrl = this.document.location?.origin ?? '';

  ngOnInit(): void {
    this.load();
  }

  protected save(): void {
    const current = this.settings();
    if (!current || this.saving()) {
      return;
    }

    this.saving.set(true);
    this.error.set(null);
    this.notice.set(null);
    this.api
      .saveSystemSettings({
        mcpEnabled: this.enabled(),
        mcpAllowWrites: this.allowWrites(),
        mcpMaxRowsPerResult: this.maxRows(),
        mcpPublicBaseUrl: this.publicBaseUrl().trim(),
        version: current.version,
      })
      .subscribe({
        next: (saved) => {
          this.apply(saved);
          this.saving.set(false);
          this.notice.set(
            'Saved. New MCP requests now use these settings; no restart is required.',
          );
        },
        error: (error: Error) => {
          this.saving.set(false);
          this.error.set(error.message);
        },
      });
  }

  protected setMaxRows(value: string): void {
    this.maxRows.set(Number.parseInt(value, 10) || 0);
  }

  /** Makes the new workspace immediately available to the adjacent catalog form and shell. */
  protected workspaceCreated(workspace: Tenant): void {
    this.catalogAdministration()?.reload(workspace.slug);
    this.resourcesChanged.emit();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.getSystemSettings().subscribe({
      next: (settings) => {
        this.apply(settings);
        this.loading.set(false);
      },
      error: (error: Error) => {
        this.loading.set(false);
        this.error.set(error.message);
      },
    });
  }

  private apply(settings: SystemSettings): void {
    this.settings.set(settings);
    this.enabled.set(settings.mcpEnabled);
    this.allowWrites.set(settings.mcpAllowWrites);
    this.maxRows.set(settings.mcpMaxRowsPerResult);
    this.publicBaseUrl.set(settings.mcpPublicBaseUrl);
  }
}
