import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { LakehouseService } from './lakehouse.service';
import { SystemSettings } from './models';

const MCP_PUBLIC_BASE_URL_MAX_LENGTH = 2048;

/**
 * Instance-wide operator settings that are applied by the API on the next MCP request.
 *
 * Everything here answers to `Capability.Instance`. People and tokens are workspace administration
 * and live under their own destination, so an owner is never sent to a page whose contents their
 * credential cannot read.
 */
@Component({
  selector: 'lh-system-settings',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './system-settings.component.html',
  styleUrls: ['./admin-page.css', './system-settings.component.css'],
})
export class SystemSettingsComponent implements OnInit {
  private readonly api = inject(LakehouseService);
  private readonly document = inject(DOCUMENT);

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
