import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject } from 'rxjs';
import { LakehouseService } from './lakehouse.service';
import { MemberAdministrationComponent } from './member-administration.component';
import { TenantMember } from './models';
import { FakeLakehouseService } from './test-doubles';

function member(overrides: Partial<TenantMember> = {}): TenantMember {
  return {
    id: 1,
    subject: 'ada',
    displayName: 'Ada Lovelace',
    email: 'ada@example.test',
    role: 'reader',
    status: 'active',
    createdUtc: '2026-08-01T10:00:00Z',
    lastSeenUtc: null,
    ...overrides,
  };
}

describe('MemberAdministrationComponent', () => {
  let api: FakeLakehouseService;
  let fixture: ComponentFixture<MemberAdministrationComponent>;

  beforeEach(() => {
    api = new FakeLakehouseService();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: LakehouseService, useValue: api }],
    });
  });

  async function mount(): Promise<void> {
    fixture = TestBed.createComponent(MemberAdministrationComponent);
    await fixture.whenStable();
  }

  function text(): string {
    return fixture.nativeElement.textContent ?? '';
  }

  it('tells an administrator that somebody is waiting on them', async () => {
    api.members = [
      member({ id: 1, subject: 'ada', status: 'active' }),
      member({ id: 2, subject: 'newcomer', displayName: 'New Comer', status: 'pending' }),
    ];

    await mount();

    // Someone signed in and reaching nothing is the only state that needs a decision. If this is
    // not said plainly, a request to join sits unanswered and looks like a broken login.
    expect(text()).toContain('1 waiting for a decision');
    expect(fixture.nativeElement.querySelectorAll('tr.pending')).toHaveLength(1);
  });

  it('admits a pending person with one action', async () => {
    api.members = [member({ id: 7, subject: 'newcomer', status: 'pending' })];
    await mount();

    const admit = [...fixture.nativeElement.querySelectorAll('button')].find(
      (b) => (b as HTMLElement).textContent?.trim() === 'Admit',
    ) as HTMLButtonElement;
    admit.click();
    await fixture.whenStable();

    expect(api.lastArgs('updateMember')).toEqual(['demo', 7, { status: 'active' }]);
  });

  it('requires confirmation before removing, but not before suspending', async () => {
    api.members = [member({ id: 3, status: 'active' })];
    await mount();

    const button = (label: string) =>
      [...fixture.nativeElement.querySelectorAll('button')].find(
        (b) => (b as HTMLElement).textContent?.trim() === label,
      ) as HTMLButtonElement;

    // Suspension is reversible and keeps the person listed, so it acts immediately.
    button('Suspend').click();
    await fixture.whenStable();
    expect(api.lastArgs('updateMember')).toEqual(['demo', 3, { status: 'suspended' }]);

    // Removal discards the record, so it asks twice.
    button('Remove').click();
    await fixture.whenStable();
    expect(api.countOf('removeMember')).toBe(0);

    button('Confirm remove').click();
    await fixture.whenStable();
    expect(api.lastArgs('removeMember')).toEqual(['demo', 3]);
  });

  it('changes a role through the picker', async () => {
    api.members = [member({ id: 4, role: 'reader' })];
    await mount();

    const picker = fixture.nativeElement.querySelector('select[aria-label^="Role for"]') as HTMLSelectElement;
    picker.value = 'owner';
    picker.dispatchEvent(new Event('change'));
    await fixture.whenStable();

    expect(api.lastArgs('updateMember')).toEqual(['demo', 4, { role: 'owner' }]);
  });

  it('ignores a listing that arrives after the workspace changed', async () => {
    api.tenants = [
      { slug: 'alpha', displayName: 'Alpha', catalogs: [] },
      { slug: 'beta', displayName: 'Beta', catalogs: [] },
    ];
    await mount();

    const pending: Subject<TenantMember[]>[] = [];
    api.listMembers = (): Observable<TenantMember[]> => {
      const reply = new Subject<TenantMember[]>();
      pending.push(reply);
      return reply.asObservable();
    };

    const workspace = fixture.nativeElement.querySelector('select[name="tenant"]') as HTMLSelectElement;
    workspace.value = 'beta';
    workspace.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    workspace.value = 'alpha';
    workspace.dispatchEvent(new Event('change'));
    await fixture.whenStable();

    // Alpha resolves first and beta -- now stale -- lands last. Rendering it would show one
    // workspace's people under another's name, and suspending from that list would act on alpha.
    pending[1].next([member({ id: 20, displayName: 'Alpha Person' })]);
    pending[0].next([member({ id: 10, displayName: 'Beta Person' })]);
    await fixture.whenStable();

    expect(text()).toContain('Alpha Person');
    expect(text()).not.toContain('Beta Person');
  });

  it('explains what to do when nobody has signed in yet', async () => {
    api.members = [];
    await mount();

    // An empty table with no explanation reads as "broken", which is how an administrator concludes
    // the product cannot add people.
    expect(text()).toContain('Nobody has signed in to this workspace yet');
  });
});
