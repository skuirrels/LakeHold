import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  output,
  signal,
} from '@angular/core';
import { LakehouseService } from './lakehouse.service';
import { Tenant } from './models';
import {
  WORKSPACE_SLUG_PATTERN,
  isWorkspaceSlug,
  normalizeWorkspaceIdentity,
} from './workspace-provisioning';

/** Creates an additional workspace from the instance administration surface. */
@Component({
  selector: 'lh-workspace-administration',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workspace-administration.component.html',
  styleUrl: './provisioning-administration.component.css',
})
export class WorkspaceAdministrationComponent {
  private readonly api = inject(LakehouseService);

  /** Announces the new workspace so every existing workspace picker can be refreshed. */
  readonly created = output<Tenant>();

  protected readonly slug = signal('');
  protected readonly displayName = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly notice = signal<string | null>(null);
  protected readonly slugPattern = WORKSPACE_SLUG_PATTERN;
  protected readonly validSlug = computed(() => isWorkspaceSlug(this.slug()));
  protected readonly validWorkspace = computed(
    () => normalizeWorkspaceIdentity(this.slug(), this.displayName()) !== null,
  );

  protected create(): void {
    const workspace = normalizeWorkspaceIdentity(this.slug(), this.displayName());
    if (!workspace || this.busy()) {
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);

    this.api.createTenant(workspace.slug, workspace.displayName).subscribe({
      next: (created) => {
        this.busy.set(false);
        this.notice.set(
          `Workspace '${created.displayName}' (${created.slug}) created. Create its first catalog below.`,
        );
        this.slug.set('');
        this.displayName.set('');
        this.created.emit(created);
      },
      error: (error: Error) => {
        this.busy.set(false);
        // Keep both values: a rejected slug or duplicate normally needs a small correction.
        this.error.set(error.message);
      },
    });
  }
}
