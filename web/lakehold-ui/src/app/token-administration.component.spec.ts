import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject } from 'rxjs';
import { LakehouseService } from './lakehouse.service';
import { ApiToken, Tenant } from './models';
import { FakeLakehouseService } from './test-doubles';
import { TokenAdministrationComponent } from './token-administration.component';

/** An active tenant credential, with only the behaviour-relevant fields stated. */
function apiToken(overrides: Partial<ApiToken> = {}): ApiToken {
  return {
    id: 1,
    name: 'token',
    scope: 'Tenant',
    role: 'Reader',
    catalogName: null,
    readOnly: true,
    createdUtc: '2026-07-29T18:00:00Z',
    expiresUtc: null,
    revokedUtc: null,
    lastUsedUtc: null,
    ...overrides,
  };
}

describe('TokenAdministrationComponent', () => {
  let api: FakeLakehouseService;
  let fixture: ComponentFixture<TokenAdministrationComponent>;

  beforeEach(() => {
    api = new FakeLakehouseService();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: LakehouseService, useValue: api }],
    });
  });

  /** A workspace as the page hands it down, with one catalog to narrow a credential to. */
  function workspace(slug = 'demo', catalog = 'analytics'): Tenant {
    return {
      slug,
      displayName: slug,
      catalogs: [{ name: catalog, dataPath: '/d', isReadOnly: false }],
    };
  }

  async function mount(selected: Tenant | null = workspace()): Promise<void> {
    fixture = TestBed.createComponent(TokenAdministrationComponent);
    // The workspace is the page's decision, so every test states it the way the page would.
    fixture.componentRef.setInput('workspace', selected);
    await fixture.whenStable();
  }

  /** Points the card at another workspace, as switching the page's picker does. */
  async function show(selected: Tenant): Promise<void> {
    fixture.componentRef.setInput('workspace', selected);
    await fixture.whenStable();
  }

  it('takes its workspace from the page and offers that workspace"s catalogs', async () => {
    await mount();

    // It must not go looking for workspaces of its own: the page owns that list and the selection,
    // and a card that chose for itself could mint a credential for a workspace nobody was looking at.
    expect(api.countOf('listTenants')).toBe(0);
    expect(fixture.nativeElement.querySelector('select[name="tenant"]')).toBeNull();
    expect(
      (fixture.nativeElement.querySelector('select[name="catalog"]') as HTMLSelectElement).options,
    ).toHaveLength(2);
    expect(
      (fixture.nativeElement.querySelector('select[name="catalog"]') as HTMLSelectElement).value,
    ).toBe('analytics');
    expect(
      (fixture.nativeElement.querySelector('select[name="role"]') as HTMLSelectElement).value,
    ).toBe('reader');
  });

  it('creates a catalog-scoped token and reveals its secret once', async () => {
    api.createdToken = { id: 7, name: 'codex-agent', token: 'lkh_demo_one-time-secret' };
    await mount();

    const name = fixture.nativeElement.querySelector(
      'input[name="token-name"]',
    ) as HTMLInputElement;
    name.value = ' codex-agent ';
    name.dispatchEvent(new Event('input'));

    const catalog = fixture.nativeElement.querySelector(
      'select[name="catalog"]',
    ) as HTMLSelectElement;
    catalog.value = 'analytics';
    catalog.dispatchEvent(new Event('change'));

    (fixture.nativeElement.querySelector('form') as HTMLFormElement).dispatchEvent(
      new Event('submit'),
    );
    await fixture.whenStable();

    expect(api.lastArgs('createToken')).toEqual([
      'demo',
      {
        name: 'codex-agent',
        role: 'reader',
        readOnly: false,
        catalogName: 'analytics',
        expiresUtc: null,
      },
    ]);
    expect(
      (
        fixture.nativeElement.querySelector(
          '[aria-label="Generated API token"]',
        ) as HTMLTextAreaElement
      ).value,
    ).toBe('lkh_demo_one-time-secret');
    expect(fixture.nativeElement.textContent).toContain('cannot be recovered');

    (fixture.nativeElement.querySelector('.dismiss') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(fixture.nativeElement.querySelector('[aria-label="Generated API token"]')).toBeNull();
  });

  it('keeps an issuance failure visible', async () => {
    api.failures.set('createToken', 'Token issuance is temporarily unavailable.');
    await mount();

    const name = fixture.nativeElement.querySelector(
      'input[name="token-name"]',
    ) as HTMLInputElement;
    name.value = 'claude-agent';
    name.dispatchEvent(new Event('input'));
    (fixture.nativeElement.querySelector('form') as HTMLFormElement).dispatchEvent(
      new Event('submit'),
    );
    await fixture.whenStable();

    expect(fixture.nativeElement.textContent).toContain(
      'Token issuance is temporarily unavailable.',
    );
    expect(fixture.nativeElement.querySelector('[aria-label="Generated API token"]')).toBeNull();
  });

  it('explains why issuance is unavailable before a workspace exists', async () => {
    await mount(null);

    expect(fixture.nativeElement.textContent).toContain(
      'Create a workspace and catalog before issuing a client token.',
    );
    expect(fixture.nativeElement.querySelector('form')).toBeNull();
  });

  it('lists client credentials and requires confirmation before revoking one', async () => {
    api.tokens = [
      {
        id: 9,
        name: 'claude-production',
        scope: 'Tenant',
        role: 'Reader',
        catalogName: 'analytics',
        readOnly: true,
        createdUtc: '2026-07-29T18:00:00Z',
        expiresUtc: null,
        revokedUtc: null,
        lastUsedUtc: null,
      },
    ];
    await mount();

    expect(fixture.nativeElement.textContent).toContain('claude-production');
    expect(fixture.nativeElement.textContent).toContain('Never');

    const revoke = fixture.nativeElement.querySelector('.revoke') as HTMLButtonElement;
    revoke.click();
    await fixture.whenStable();
    expect(api.countOf('revokeToken')).toBe(0);
    expect(revoke.textContent).toContain('Confirm revoke');

    revoke.click();
    await fixture.whenStable();

    expect(api.lastArgs('revokeToken')).toEqual(['demo', 9]);
    expect(api.countOf('listTokens')).toBe(2);
  });

  it('ignores a credential listing that arrives after the workspace changed', async () => {
    await mount(workspace('alpha', 'a'));

    // Take listing over so both replies can be held open at once, which is what lets the slower
    // one land last.
    const pending: Subject<ApiToken[]>[] = [];
    api.listTokens = (): Observable<ApiToken[]> => {
      const reply = new Subject<ApiToken[]>();
      pending.push(reply);
      return reply.asObservable();
    };

    await show(workspace('beta', 'b'));
    await show(workspace('alpha', 'a'));

    // Alpha's listing resolves first and beta's — the one now stale — lands last. Order matters:
    // if the stale reply arrived first the final state would be correct with or without the guard,
    // so only a late stale reply proves anything. Rendering it would show one workspace's
    // credentials under another's name, and revoking from that list would act on alpha.
    pending[1].next([apiToken({ id: 2, name: 'alpha-credential' })]);
    pending[0].next([apiToken({ id: 1, name: 'beta-credential' })]);
    await fixture.whenStable();

    expect(fixture.nativeElement.textContent).toContain('alpha-credential');
    expect(fixture.nativeElement.textContent).not.toContain('beta-credential');
  });

  it('refuses an expiry that has already passed instead of minting a dead credential', async () => {
    await mount();

    const name = fixture.nativeElement.querySelector(
      'input[name="token-name"]',
    ) as HTMLInputElement;
    name.value = 'stale';
    name.dispatchEvent(new Event('input'));

    const expires = fixture.nativeElement.querySelector(
      'input[name="expires"]',
    ) as HTMLInputElement;
    expires.value = '2020-01-01T00:00';
    expires.dispatchEvent(new Event('input'));

    (fixture.nativeElement.querySelector('form') as HTMLFormElement).dispatchEvent(
      new Event('submit'),
    );
    await fixture.whenStable();

    expect(api.countOf('createToken')).toBe(0);
    expect(fixture.nativeElement.textContent).toContain('Expiry must be in the future.');
  });
});
