import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { LakehouseService } from './lakehouse.service';
import { MemberStatus, Tenant, TenantMember, TokenRole } from './models';

/**
 * The people who may reach a workspace.
 *
 * This is the surface that was missing when "how do I add a user?" had no answer. Identity still
 * comes from the provider — LakeHold never holds a password — but who reaches what is decided here,
 * so admitting, demoting, and revoking are things an administrator does in the product rather than
 * by editing claim mappers and hoping.
 */
@Component({
  selector: 'lh-member-administration',
  imports: [DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './member-administration.component.html',
  styleUrl: './member-administration.component.css',
})
export class MemberAdministrationComponent implements OnInit {
  private readonly api = inject(LakehouseService);

  protected readonly tenants = signal<Tenant[]>([]);
  protected readonly tenantSlug = signal('');
  protected readonly members = signal<TenantMember[]>([]);
  protected readonly loading = signal(true);
  protected readonly busyId = signal<number | null>(null);
  protected readonly pendingRemovalId = signal<number | null>(null);
  protected readonly error = signal<string | null>(null);

  /**
   * Discriminates listings so a slower reply for the previous workspace cannot overwrite a newer
   * one — the same hazard the credential list had, and with the same consequence: acting on a row
   * that belongs to a workspace you are no longer looking at.
   */
  private generation = 0;

  protected readonly roles: TokenRole[] = ['reader', 'editor', 'owner'];

  /** People waiting on a decision. Surfaced separately because it is the only actionable state. */
  protected readonly pending = computed(() =>
    this.members().filter((member) => member.status === 'pending'),
  );

  ngOnInit(): void {
    this.loadTenants();
  }

  protected loadTenants(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.listTenants().subscribe({
      next: (tenants) => {
        this.tenants.set(tenants);
        const selected = tenants.find((t) => t.slug === this.tenantSlug()) ?? tenants[0] ?? null;
        this.tenantSlug.set(selected?.slug ?? '');
        this.pendingRemovalId.set(null);
        this.loading.set(false);
        this.loadMembers();
      },
      error: (error: Error) => {
        this.loading.set(false);
        this.error.set(error.message);
      },
    });
  }

  protected selectTenant(slug: string): void {
    this.tenantSlug.set(slug);
    this.pendingRemovalId.set(null);
    this.loadMembers();
  }

  protected setRole(member: TenantMember, role: string): void {
    this.change(member, { role: role as TokenRole });
  }

  protected approve(member: TenantMember): void {
    this.change(member, { status: 'active' });
  }

  protected setStatus(member: TenantMember, status: MemberStatus): void {
    this.change(member, { status });
  }

  /**
   * Removal takes two clicks; suspension takes one.
   *
   * Suspending is reversible and keeps the person listed against their past activity. Removing
   * discards the record, so it asks twice — and the two are offered separately rather than collapsed
   * into "delete", because they answer different questions.
   */
  protected requestRemoval(member: TenantMember): void {
    if (this.busyId() !== null) {
      return;
    }

    if (this.pendingRemovalId() !== member.id) {
      this.pendingRemovalId.set(member.id);
      return;
    }

    const tenant = this.tenantSlug();
    this.busyId.set(member.id);
    this.error.set(null);
    this.api.removeMember(tenant, member.id).subscribe({
      next: () => {
        this.busyId.set(null);
        this.pendingRemovalId.set(null);
        this.loadMembers();
      },
      error: (error: Error) => {
        this.busyId.set(null);
        this.error.set(error.message);
      },
    });
  }

  protected displayNameOf(member: TenantMember): string {
    return member.displayName ?? member.email ?? member.subject;
  }

  private change(member: TenantMember, change: { role?: TokenRole; status?: MemberStatus }): void {
    if (this.busyId() !== null) {
      return;
    }

    this.busyId.set(member.id);
    this.error.set(null);
    this.api.updateMember(this.tenantSlug(), member.id, change).subscribe({
      next: () => {
        this.busyId.set(null);
        this.loadMembers();
      },
      error: (error: Error) => {
        this.busyId.set(null);
        this.error.set(error.message);
      },
    });
  }

  private loadMembers(): void {
    const generation = ++this.generation;
    const tenant = this.tenantSlug();
    if (!tenant) {
      this.members.set([]);
      return;
    }

    this.api.listMembers(tenant).subscribe({
      next: (members) => {
        if (generation !== this.generation) {
          return;
        }

        this.members.set(members);
      },
      error: (error: Error) => {
        if (generation !== this.generation) {
          return;
        }

        this.error.set(error.message);
      },
    });
  }
}
