import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { LakehouseService } from './lakehouse.service';
import { FakeLakehouseService } from './test-doubles';
import { TokenAdministrationComponent } from './token-administration.component';

describe('TokenAdministrationComponent', () => {
  let api: FakeLakehouseService;
  let fixture: ComponentFixture<TokenAdministrationComponent>;

  beforeEach(() => {
    api = new FakeLakehouseService();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: LakehouseService, useValue: api }],
    });
  });

  async function mount(): Promise<void> {
    fixture = TestBed.createComponent(TokenAdministrationComponent);
    await fixture.whenStable();
  }

  it('loads workspace and catalog choices with reader as the safe default', async () => {
    await mount();

    expect(api.countOf('listTenants')).toBe(1);
    expect(
      (fixture.nativeElement.querySelector('select[name="tenant"]') as HTMLSelectElement).value,
    ).toBe('demo');
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
    api.tenants = [];
    await mount();

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
});
