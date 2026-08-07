import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { LakehouseService } from './lakehouse.service';
import { UsersComponent } from './users.component';
import { FakeLakehouseService } from './test-doubles';

describe('UsersComponent', () => {
  let api: FakeLakehouseService;
  let fixture: ComponentFixture<UsersComponent>;

  beforeEach(() => {
    api = new FakeLakehouseService();
    api.tenants = [
      {
        slug: 'alpha',
        displayName: 'Alpha',
        catalogs: [
          { name: 'a', dataPath: '/a', isReadOnly: false, storageKind: 'Local', storageProfile: null },
        ],
      },
      {
        slug: 'beta',
        displayName: 'Beta',
        catalogs: [
          { name: 'b', dataPath: '/b', isReadOnly: false, storageKind: 'Local', storageProfile: null },
        ],
      },
    ];
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: LakehouseService, useValue: api }],
    });
  });

  async function mount(): Promise<void> {
    fixture = TestBed.createComponent(UsersComponent);
    await fixture.whenStable();
  }

  function picker(): HTMLSelectElement {
    return fixture.nativeElement.querySelector('select[name="workspace"]') as HTMLSelectElement;
  }

  async function choose(slug: string): Promise<void> {
    const select = picker();
    select.value = slug;
    select.dispatchEvent(new Event('change'));
    await fixture.whenStable();
  }

  it('asks for the workspaces once and offers exactly one picker for the page', async () => {
    await mount();

    expect(fixture.nativeElement.querySelector('h1')?.textContent).toBe('Users');
    // Two cards that each loaded the list cost two identical requests to render one page.
    expect(api.countOf('listTenants')).toBe(1);
    expect(fixture.nativeElement.querySelectorAll('select[name="workspace"]')).toHaveLength(1);
    // Neither card may keep a picker of its own; that is what let them disagree.
    expect(fixture.nativeElement.querySelectorAll('select[name="tenant"]')).toHaveLength(0);
  });

  it('moves both cards to the same workspace', async () => {
    await mount();

    expect(api.lastArgs('listMembers')).toEqual(['alpha']);
    expect(api.lastArgs('listTokens')).toEqual(['alpha']);

    await choose('beta');

    // The bug this page exists to prevent: the member list showing one workspace while the token
    // form mints a credential for another, under a heading that names neither.
    expect(api.lastArgs('listMembers')).toEqual(['beta']);
    expect(api.lastArgs('listTokens')).toEqual(['beta']);
  });

  it('narrows a credential to a catalog of the workspace now selected', async () => {
    await mount();
    await choose('beta');

    const catalog = fixture.nativeElement.querySelector(
      'select[name="catalog"]',
    ) as HTMLSelectElement;
    expect([...catalog.options].map((option) => option.value)).toEqual(['', 'b']);
    expect(catalog.value).toBe('b');
  });

  it('offers to retry when the workspaces cannot be read, and renders no cards', async () => {
    api.failures.set('listTenants', 'Workspaces are temporarily unavailable.');
    await mount();

    expect(fixture.nativeElement.textContent).toContain('Workspaces are temporarily unavailable.');
    expect(fixture.nativeElement.querySelector('lh-member-administration')).toBeNull();

    api.failures.delete('listTenants');
    (fixture.nativeElement.querySelector('.retry') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(fixture.nativeElement.querySelector('lh-member-administration')).toBeTruthy();
    expect(api.lastArgs('listMembers')).toEqual(['alpha']);
  });

  it('says a workspace has to exist first rather than showing two empty cards', async () => {
    api.tenants = [];
    await mount();

    expect(fixture.nativeElement.textContent).toContain('There are no workspaces yet');
    expect(fixture.nativeElement.querySelector('lh-member-administration')).toBeNull();
    expect(fixture.nativeElement.querySelector('lh-token-administration')).toBeNull();
  });
});
