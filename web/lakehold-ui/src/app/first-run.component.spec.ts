import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FirstRunComponent, SignInRequest, WorkspaceRequest } from './first-run.component';

describe('FirstRunComponent', () => {
  let fixture: ComponentFixture<FirstRunComponent>;

  async function mount(mode: 'unauthorized' | 'setup' = 'setup') {
    fixture = TestBed.createComponent(FirstRunComponent);
    fixture.componentRef.setInput('mode', mode);
    await fixture.whenStable();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  });

  it('trims a credential, emits it once, and clears the draft', async () => {
    await mount('unauthorized');
    const received: SignInRequest[] = [];
    fixture.componentInstance.signIn.subscribe((request) => received.push(request));
    const input = fixture.nativeElement.querySelector('input[type="password"]') as HTMLInputElement;
    input.value = '  lkh_bootstrap  ';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();

    (fixture.nativeElement.querySelector('.actions button') as HTMLButtonElement).click();
    await fixture.whenStable();

    // Session-scoped unless the operator asks otherwise, so the safer lifetime is the one you get
    // without making a decision.
    expect(received).toEqual([{ token: 'lkh_bootstrap', persist: false }]);
    expect(input.value).toBe('');
  });

  it('carries the keep-me-signed-in choice with the credential', async () => {
    await mount('unauthorized');
    const received: SignInRequest[] = [];
    fixture.componentInstance.signIn.subscribe((request) => received.push(request));

    const input = fixture.nativeElement.querySelector('input[type="password"]') as HTMLInputElement;
    input.value = 'lkh_bootstrap';
    input.dispatchEvent(new Event('input'));

    const remember = fixture.nativeElement.querySelector(
      '.remember input[type="checkbox"]',
    ) as HTMLInputElement;
    remember.checked = true;
    remember.dispatchEvent(new Event('change'));
    await fixture.whenStable();

    (fixture.nativeElement.querySelector('.actions button') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(received).toEqual([{ token: 'lkh_bootstrap', persist: true }]);
  });

  it('offers browser OIDC without removing the break-glass token path', async () => {
    await mount('unauthorized');
    fixture.componentRef.setInput('oidcEnabled', true);
    await fixture.whenStable();

    const link = fixture.nativeElement.querySelector('.oidc-sign-in') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('/auth/login?returnUrl=/workbench');
    expect(fixture.nativeElement.querySelector('input[type="password"]')).toBeTruthy();
  });

  it('uses the slug as the display name when the optional name is blank', async () => {
    await mount();
    const received: WorkspaceRequest[] = [];
    fixture.componentInstance.createWorkspace.subscribe((request) => received.push(request));
    const [slug, displayName, catalog] = fixture.nativeElement.querySelectorAll(
      'input[type="text"]',
    ) as NodeListOf<HTMLInputElement>;
    for (const [input, value] of [
      [slug, '  acme  '],
      [displayName, '   '],
      [catalog, ' warehouse '],
    ] as const) {
      input.value = value;
      input.dispatchEvent(new Event('input'));
    }
    await fixture.whenStable();

    (fixture.nativeElement.querySelector('.actions button') as HTMLButtonElement).click();

    expect(received).toEqual([{ slug: 'acme', displayName: 'acme', catalog: 'warehouse' }]);
  });

  it('prevents duplicate setup while provisioning is busy', async () => {
    await mount();
    fixture.componentRef.setInput('busy', true);
    await fixture.whenStable();

    expect(
      (fixture.nativeElement.querySelector('.actions button') as HTMLButtonElement).disabled,
    ).toBe(true);
  });

  it('shows the one-time token instead of another setup form', async () => {
    await mount();
    fixture.componentRef.setInput('issuedToken', 'lkh_acme_once');
    fixture.componentRef.setInput('workspace', 'acme');
    await fixture.whenStable();

    expect(fixture.nativeElement.textContent).toContain('Workspace ready');
    expect(fixture.nativeElement.textContent).toContain('lkh_acme_once');
    expect(fixture.nativeElement.querySelector('input')).toBeNull();
  });
});
