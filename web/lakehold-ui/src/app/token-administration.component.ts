import { DOCUMENT } from '@angular/common';
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
import { LakehouseService } from './lakehouse.service';
import { ApiToken, CreatedToken, Tenant, TokenRole } from './models';

/**
 * Minting and revoking least-privilege tenant API tokens; a workspace owner administers its own.
 *
 * The workspace comes from the page, which owns the one picker both its cards obey. Catalog scope
 * stays here, because narrowing a credential to a single catalog is a property of the credential
 * rather than of what the page is looking at.
 */
@Component({
  selector: 'lh-token-administration',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './token-administration.component.html',
  styleUrl: './token-administration.component.css',
})
export class TokenAdministrationComponent {
  private readonly api = inject(LakehouseService);
  private readonly document = inject(DOCUMENT);

  readonly workspace = input<Tenant | null>(null);

  protected readonly catalogName = signal('');
  protected readonly tokenName = signal('');
  protected readonly role = signal<TokenRole>('reader');
  protected readonly readOnly = signal(false);
  protected readonly expiresLocal = signal('');
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
  private workspaceGeneration = 0;

  protected readonly canCreate = computed(() => {
    const nameLength = this.tokenName().trim().length;
    return !this.creating() && this.workspace() !== null && nameLength >= 1 && nameLength <= 200;
  });

  constructor() {
    // Same shape as the workbench panels: depend on exactly the input, do the work `untracked`.
    effect(() => {
      const workspace = this.workspace();
      this.workspaceGeneration += 1;
      untracked(() => {
        this.tokens.set([]);
        this.error.set(null);
        // A pending "click again to confirm" is scoped to the list it was armed against, and an id
        // armed against the old list would revoke whichever credential shares it in the new one.
        this.pendingRevokeId.set(null);
        this.creating.set(false);
        this.revokingId.set(null);
        // The one-time secret on screen was minted for the workspace being left. Keeping it visible
        // beside another workspace's credentials invites pasting it where it does not work.
        this.createdToken.set(null);
        this.copyStatus.set(null);
        // Keep the chosen catalog where the new workspace also has one by that name; otherwise the
        // narrowest scope it can offer, which is its first catalog.
        const catalog = this.catalogName();
        this.catalogName.set(
          workspace?.catalogs.some((candidate) => candidate.name === catalog)
            ? catalog
            : (workspace?.catalogs[0]?.name ?? ''),
        );
        this.loadTokens();
      });
    });
  }

  protected create(): void {
    const name = this.tokenName().trim();
    const tenant = this.slug();
    const workspaceGeneration = this.workspaceGeneration;
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
          if (workspaceGeneration !== this.workspaceGeneration) {
            return;
          }

          this.creating.set(false);
          this.createdToken.set(created);
          this.loadTokens();
        },
        error: (error: Error) => {
          if (workspaceGeneration !== this.workspaceGeneration) {
            return;
          }

          this.creating.set(false);
          this.error.set(error.message);
        },
      });
  }

  protected async copyToken(): Promise<void> {
    const token = this.createdToken()?.token;
    const clipboard = this.document.defaultView?.navigator.clipboard;
    const workspaceGeneration = this.workspaceGeneration;
    if (!token || !clipboard) {
      this.copyStatus.set('Automatic copy is unavailable. Select and copy the token manually.');
      return;
    }

    try {
      await clipboard.writeText(token);
      if (workspaceGeneration === this.workspaceGeneration) {
        this.copyStatus.set('Token copied.');
      }
    } catch {
      if (workspaceGeneration === this.workspaceGeneration) {
        this.copyStatus.set('Automatic copy failed. Select and copy the token manually.');
      }
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

    const tenant = this.slug();
    const workspaceGeneration = this.workspaceGeneration;
    this.revokingId.set(id);
    this.error.set(null);
    this.api.revokeToken(tenant, id).subscribe({
      next: () => {
        if (workspaceGeneration !== this.workspaceGeneration) {
          return;
        }

        this.revokingId.set(null);
        this.pendingRevokeId.set(null);
        this.loadTokens();
      },
      error: (error: Error) => {
        if (workspaceGeneration !== this.workspaceGeneration) {
          return;
        }

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

  private slug(): string {
    return this.workspace()?.slug ?? '';
  }

  /** Re-reads the issued credentials. Protected because both Refresh controls call it. */
  protected loadTokens(): void {
    const generation = ++this.tokenRequestGeneration;
    const tenant = this.slug();
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
