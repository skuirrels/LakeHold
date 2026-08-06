import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { LakehouseService } from './lakehouse.service';
import { MemberAdministrationComponent } from './member-administration.component';
import { Tenant } from './models';
import { TokenAdministrationComponent } from './token-administration.component';

/**
 * Who and what may reach a workspace: the people admitted from the identity provider and the tokens
 * issued to clients.
 *
 * Its own destination rather than a section of System Settings, because the two answer to different
 * credentials. Everything here is `Capability.TenantAdmin` — a workspace owner administers it — while
 * the settings page is instance-scoped. Sharing one page meant an owner opened a surface whose first
 * card they were not allowed to read.
 *
 * The page owns the workspace list and the selection, and its cards take both as inputs. When each
 * card loaded and chose for itself, they could disagree: the member list showed one workspace and the
 * token form minted a credential for another, under a single heading that named neither. It also cost
 * two identical `listTenants` requests to render one page.
 */
@Component({
  selector: 'lh-people',
  imports: [MemberAdministrationComponent, TokenAdministrationComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './people.component.html',
  styleUrls: ['./admin-page.css', './people.component.css'],
})
export class PeopleComponent implements OnInit {
  private readonly api = inject(LakehouseService);

  protected readonly tenants = signal<Tenant[]>([]);
  protected readonly selectedSlug = signal('');
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  /** The selection both cards obey. Null only while loading, on failure, or on an empty instance. */
  protected readonly workspace = computed(
    () => this.tenants().find((tenant) => tenant.slug === this.selectedSlug()) ?? null,
  );

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.listTenants().subscribe({
      next: (tenants) => {
        this.tenants.set(tenants);
        // Keep the operator where they were if a reload still offers it; a workspace that has gone
        // falls back to the first rather than leaving the cards pointed at nothing.
        const kept = tenants.find((tenant) => tenant.slug === this.selectedSlug());
        this.selectedSlug.set((kept ?? tenants[0])?.slug ?? '');
        this.loading.set(false);
      },
      error: (error: Error) => {
        this.loading.set(false);
        this.error.set(error.message);
      },
    });
  }

  protected select(slug: string): void {
    this.selectedSlug.set(slug);
  }
}
