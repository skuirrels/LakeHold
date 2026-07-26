import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FirstRunComponent, WorkspaceRequest } from './first-run.component';

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
    const received: string[] = [];
    fixture.componentInstance.signIn.subscribe((token) => received.push(token));
    const input = fixture.nativeElement.querySelector('input[type="password"]') as HTMLInputElement;
    input.value = '  lkh_bootstrap  ';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();

    (fixture.nativeElement.querySelector('.actions button') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(received).toEqual(['lkh_bootstrap']);
    expect(input.value).toBe('');
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
