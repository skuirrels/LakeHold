import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  inject,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { CatalogPlacementComponent } from './catalog-placement.component';
import { LakehouseService } from './lakehouse.service';
import { Tenant } from './models';

/**
 * Creates a catalog in an existing workspace.
 *
 * First run creates the first catalog and then never appears again, which left the second one
 * reachable only through the HTTP API. This is that same operation with the same placement form —
 * the component is shared rather than the markup copied, so the two cannot come to disagree about
 * which profiles serve which schemes.
 */
@Component({
  selector: 'lh-catalog-administration',
  imports: [CatalogPlacementComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './catalog-administration.component.html',
  styleUrl: './catalog-administration.component.css',
})
export class CatalogAdministrationComponent implements OnInit {
  private readonly api = inject(LakehouseService);

  /**
   * Announces a catalog that now exists.
   *
   * The workbench loads its workspace list once and does not reload it when navigation returns from
   * System Settings, so without this the catalog an operator just created is missing from the picker
   * until the page is reloaded — created successfully and apparently absent.
   */
  readonly created = output<void>();

  protected readonly tenants = signal<Tenant[]>([]);
  protected readonly tenantSlug = signal('');
  protected readonly name = signal('');
  protected readonly loading = signal(true);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly notice = signal<string | null>(null);

  private readonly placement = viewChild(CatalogPlacementComponent);

  ngOnInit(): void {
    this.api.listTenants().subscribe({
      next: (tenants) => {
        this.tenants.set(tenants);
        this.tenantSlug.set(tenants[0]?.slug ?? '');
        this.loading.set(false);
      },
      error: (error: Error) => {
        this.loading.set(false);
        this.error.set(error.message);
      },
    });
  }

  protected create(): void {
    const tenant = this.tenantSlug();
    const name = this.name().trim();
    if (!tenant || !name || this.busy()) {
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);

    // Read at submit, like first run does, so the request carries the form as it stands rather
    // than a copy taken when some earlier change happened to fire.
    const placement = this.placement()?.value();
    const explicit = placement?.dataPath || placement?.storageProfile || placement?.readOnly;

    this.api.createCatalog(tenant, name, explicit ? placement : undefined).subscribe({
      next: () => {
        this.busy.set(false);
        this.notice.set(`Catalog '${name}' created in ${tenant}.`);
        this.name.set('');
        this.created.emit();
      },
      error: (error: Error) => {
        this.busy.set(false);
        // Left populated on purpose: a rejected placement is usually one character from a working
        // one, and clearing the form would make the operator retype a URI to find out.
        this.error.set(error.message);
      },
    });
  }
}
