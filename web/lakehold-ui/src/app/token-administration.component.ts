import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { LakehouseService } from './lakehouse.service';
import { ApiToken, CreatedToken, Tenant, TokenRole } from './models';

/** Instance-administrator workflow for minting least-privilege tenant API tokens. */
@Component({
  selector: 'lh-token-administration',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './token-administration.component.html',
  styleUrl: './token-administration.component.css',
})
export class TokenAdministrationComponent implements OnInit {
  private readonly api = inject(LakehouseService);
  private readonly document = inject(DOCUMENT);

  protected readonly tenants = signal<Tenant[]>([]);
  protected readonly tenantSlug = signal('');
  protected readonly catalogName = signal('');
  protected readonly tokenName = signal('');
  protected readonly role = signal<TokenRole>('reader');
  protected readonly readOnly = signal(false);
  protected readonly expiresLocal = signal('');
  protected readonly loading = signal(true);
  protected readonly creating = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly createdToken = signal<CreatedToken | null>(null);
  protected readonly copyStatus = signal<string | null>(null);
  protected readonly tokens = signal<ApiToken[]>([]);
  protected readonly tokensLoading = signal(false);
  protected readonly pendingRevokeId = signal<number | null>(null);
  protected readonly revokingId = signal<number | null>(null);

  /**
   * Discriminates listings so a slower reply for the previous workspace cannot overwrite a newer
   * one. Switching workspaces twice in quick succession otherwise renders one tenant's credentials
   * under another's name, and revoking from that list would act on the workspace now selected.
   */
  private tokenRequestGeneration = 0;

  protected readonly selectedTenant = computed(
    () => this.tenants().find((tenant) => tenant.slug === this.tenantSlug()) ?? null,
  );
  protected readonly canCreate = computed(() => {
    const nameLength = this.tokenName().trim().length;
    return !this.creating() && this.tenantSlug().length > 0 && nameLength >= 1 && nameLength <= 200;
  });

  ngOnInit(): void {
    this.loadTenants();
  }

  protected loadTenants(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.listTenants().subscribe({
      next: (tenants) => {
        this.tenants.set(tenants);
        const selected =
          tenants.find((tenant) => tenant.slug === this.tenantSlug()) ?? tenants[0] ?? null;
        this.tenantSlug.set(selected?.slug ?? '');
        // A pending "click again to confirm" is scoped to the list it was armed against. Reloading
        // the workspaces can move the selection — a deleted tenant falls back to the first — and an
        // id armed against the old list would then revoke whichever credential shares that id here.
        this.pendingRevokeId.set(null);
        const catalog = this.catalogName();
        this.catalogName.set(
          selected?.catalogs.some((candidate) => candidate.name === catalog)
            ? catalog
            : (selected?.catalogs[0]?.name ?? ''),
        );
        this.loading.set(false);
        this.loadTokens();
      },
      error: (error: Error) => {
        this.loading.set(false);
        this.error.set(error.message);
      },
    });
  }

  protected selectTenant(slug: string): void {
    this.tenantSlug.set(slug);
    this.catalogName.set(
      this.tenants().find((tenant) => tenant.slug === slug)?.catalogs[0]?.name ?? '',
    );
    this.pendingRevokeId.set(null);
    this.loadTokens();
  }

  protected create(): void {
    const name = this.tokenName().trim();
    const tenant = this.tenantSlug();
    if (this.creating() || tenant.length === 0 || name.length < 1 || name.length > 200) {
      return;
    }

    const expiresUtc = this.expirationUtc();
    if (expiresUtc === undefined) {
      this.error.set('Expiry must be a valid date and time.');
      return;
    }

    // The API accepts any timestamp, so a past expiry mints a credential that is dead on arrival
    // and reports success. Refuse here rather than hand back a token that can never authenticate.
    if (expiresUtc !== null && new Date(expiresUtc).getTime() <= Date.now()) {
      this.error.set('Expiry must be in the future.');
      return;
    }

    this.creating.set(true);
    this.error.set(null);
    this.createdToken.set(null);
    this.copyStatus.set(null);
    this.api
      .createToken(tenant, {
        name,
        role: this.role(),
        readOnly: this.readOnly(),
        catalogName: this.catalogName() || null,
        expiresUtc,
      })
      .subscribe({
        next: (created) => {
          this.creating.set(false);
          this.createdToken.set(created);
          this.loadTokens();
        },
        error: (error: Error) => {
          this.creating.set(false);
          this.error.set(error.message);
        },
      });
  }

  protected async copyToken(): Promise<void> {
    const token = this.createdToken()?.token;
    const clipboard = this.document.defaultView?.navigator.clipboard;
    if (!token || !clipboard) {
      this.copyStatus.set('Automatic copy is unavailable. Select and copy the token manually.');
      return;
    }

    try {
      await clipboard.writeText(token);
      this.copyStatus.set('Token copied.');
    } catch {
      this.copyStatus.set('Automatic copy failed. Select and copy the token manually.');
    }
  }

  protected dismissToken(): void {
    this.createdToken.set(null);
    this.copyStatus.set(null);
  }

  protected requestRevoke(id: number): void {
    if (this.revokingId() !== null) {
      return;
    }

    if (this.pendingRevokeId() !== id) {
      this.pendingRevokeId.set(id);
      return;
    }

    const tenant = this.tenantSlug();
    this.revokingId.set(id);
    this.error.set(null);
    this.api.revokeToken(tenant, id).subscribe({
      next: () => {
        this.revokingId.set(null);
        this.pendingRevokeId.set(null);
        this.loadTokens();
      },
      error: (error: Error) => {
        this.revokingId.set(null);
        this.error.set(error.message);
      },
    });
  }

  protected tokenStatus(token: ApiToken): 'Active' | 'Expired' | 'Revoked' {
    if (token.revokedUtc) {
      return 'Revoked';
    }

    return token.expiresUtc && new Date(token.expiresUtc).getTime() <= Date.now()
      ? 'Expired'
      : 'Active';
  }

  private loadTokens(): void {
    const generation = ++this.tokenRequestGeneration;
    const tenant = this.tenantSlug();
    if (!tenant) {
      this.tokens.set([]);
      this.tokensLoading.set(false);
      return;
    }

    this.tokensLoading.set(true);
    this.api.listTokens(tenant).subscribe({
      next: (tokens) => {
        if (generation !== this.tokenRequestGeneration) {
          return;
        }

        this.tokens.set(tokens);
        this.tokensLoading.set(false);
      },
      error: (error: Error) => {
        if (generation !== this.tokenRequestGeneration) {
          return;
        }

        this.tokensLoading.set(false);
        this.error.set(error.message);
      },
    });
  }

  private expirationUtc(): string | null | undefined {
    const value = this.expiresLocal().trim();
    if (value.length === 0) {
      return null;
    }

    const parsed = new Date(value);
    return Number.isNaN(parsed.getTime()) ? undefined : parsed.toISOString();
  }
}
