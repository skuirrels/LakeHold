import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ThemeToggleComponent } from './theme-toggle.component';
import { THEME_STORAGE_KEY, ThemeService } from './theme.service';

describe('ThemeToggleComponent', () => {
  beforeEach(() => {
    localStorage.removeItem(THEME_STORAGE_KEY);
    document.documentElement.removeAttribute('data-theme');
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  });

  afterEach(() => {
    localStorage.removeItem(THEME_STORAGE_KEY);
    document.documentElement.removeAttribute('data-theme');
  });

  async function render() {
    const fixture = TestBed.createComponent(ThemeToggleComponent);
    await fixture.whenStable();
    return fixture;
  }

  function button(fixture: { nativeElement: HTMLElement }): HTMLButtonElement {
    return fixture.nativeElement.querySelector('button') as HTMLButtonElement;
  }

  it('offers the light theme while the app is dark, and says so in words', async () => {
    // The icon alone is the ambiguous part of this control — it can equally be read as "you are in
    // dark mode". The accessible name has to be the unambiguous one.
    const fixture = await render();

    expect(button(fixture).getAttribute('aria-label')).toBe('Switch to light theme');
    expect(button(fixture).getAttribute('aria-pressed')).toBe('false');
    expect(fixture.nativeElement.querySelector('circle')).toBeTruthy();
  });

  it('switches the palette and flips its own label when pressed', async () => {
    const fixture = await render();

    button(fixture).click();
    await fixture.whenStable();

    expect(TestBed.inject(ThemeService).theme()).toBe('light');
    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
    expect(button(fixture).getAttribute('aria-label')).toBe('Switch to dark theme');
    expect(button(fixture).getAttribute('aria-pressed')).toBe('true');
    // The sun's disc is gone: the moon is the only path left.
    expect(fixture.nativeElement.querySelector('circle')).toBeFalsy();
  });

  it('opens showing the palette already in force', async () => {
    localStorage.setItem(THEME_STORAGE_KEY, 'light');

    const fixture = await render();

    expect(button(fixture).getAttribute('aria-label')).toBe('Switch to dark theme');
    expect(button(fixture).getAttribute('aria-pressed')).toBe('true');
  });
});
