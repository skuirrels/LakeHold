import { DatePipe } from '@angular/common';
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
import { MemberStatus, Tenant, TenantMember, TokenRole } from './models';

/**
 * The people who may reach a workspace.
 *
 * This is the surface that was missing when "how do I add a user?" had no answer. Identity still
 * comes from the provider — LakeHold never holds a password — but who reaches what is decided here,
 * so admitting, demoting, and revoking are things an administrator does in the product rather than
 * by editing claim mappers and hoping.
 *
 * Which workspace it administers is the page's decision, not its own. It sits beside the token card,
 * and two cards owning a picker each meant one could be administering a workspace the other was not
 * showing.
 */
@Component({
  selector: 'lh-member-administration',
  imports: [DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './member-administration.component.html',
  styleUrl: './member-administration.component.css',
})
export class MemberAdministrationComponent {
  private readonly api = inject(LakehouseService);

  readonly workspace = input<Tenant | null>(null);

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

  constructor() {
    // The same shape the workbench panels use: depend on exactly the input, and do the work
    // `untracked` so nothing the reload happens to read becomes a dependency of the reload.
    effect(() => {
      this.workspace();
      untracked(() => {
        this.members.set([]);
        // A failure that belonged to the previous workspace would otherwise stand over this one.
        this.error.set(null);
        this.busyId.set(null);
        // An armed "Confirm remove" belongs to the list it was armed against. Left armed across a
        // workspace change, the second click removes whoever holds that id in the new one.
        this.pendingRemovalId.set(null);
        this.loadMembers();
      });
    });
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

    const tenant = this.slug();
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
    this.api.updateMember(this.slug(), member.id, change).subscribe({
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

  private slug(): string {
    return this.workspace()?.slug ?? '';
  }

  private loadMembers(): void {
    const generation = ++this.generation;
    const tenant = this.slug();
    if (!tenant) {
      this.members.set([]);
      this.loading.set(false);
      return;
    }

    this.loading.set(true);
    this.api.listMembers(tenant).subscribe({
      next: (members) => {
        if (generation !== this.generation) {
          return;
        }

        this.members.set(members);
        this.loading.set(false);
      },
      error: (error: Error) => {
        if (generation !== this.generation) {
          return;
        }

        this.loading.set(false);
        this.error.set(error.message);
      },
    });
  }
}
