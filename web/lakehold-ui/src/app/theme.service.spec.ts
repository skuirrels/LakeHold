import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { THEME_STORAGE_KEY, ThemeService } from './theme.service';

describe('ThemeService', () => {
  beforeEach(() => {
    localStorage.removeItem(THEME_STORAGE_KEY);
    document.documentElement.removeAttribute('data-theme');
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  });

  afterEach(() => {
    localStorage.removeItem(THEME_STORAGE_KEY);
    document.documentElement.removeAttribute('data-theme');
  });

  it('starts dark when nothing has been chosen', () => {
    // Dark is what this product shipped with. A first visit must not be repainted by an OS
    // preference nobody expressed here.
    const service = TestBed.inject(ThemeService);

    expect(service.theme()).toBe('dark');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
  });

  it('restores the palette chosen on a previous visit', () => {
    localStorage.setItem(THEME_STORAGE_KEY, 'light');

    const service = TestBed.inject(ThemeService);

    expect(service.theme()).toBe('light');
    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
  });

  it('falls back to dark when the stored value is not a palette', () => {
    // The key is public and shares its name with the pre-paint script; anything could have written
    // it. An unrecognised value must not leave the document with no palette at all.
    localStorage.setItem(THEME_STORAGE_KEY, 'sepia');

    expect(TestBed.inject(ThemeService).theme()).toBe('dark');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
  });

  it('drives the document attribute and remembers each switch', () => {
    const service = TestBed.inject(ThemeService);

    service.toggle();
    expect(service.theme()).toBe('light');
    expect(service.isLight()).toBe(true);
    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('light');

    service.toggle();
    expect(service.theme()).toBe('dark');
    expect(service.isLight()).toBe(false);
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark');
  });

  it('still applies a palette when storage is unavailable', () => {
    // Safari in private browsing throws on access rather than returning null, which would otherwise
    // take down whichever component happened to inject the service first.
    const getItem = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied');
    });
    const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('denied');
    });

    try {
      const service = TestBed.inject(ThemeService);
      expect(service.theme()).toBe('dark');

      service.toggle();
      expect(service.theme()).toBe('light');
      expect(document.documentElement.getAttribute('data-theme')).toBe('light');
    } finally {
      getItem.mockRestore();
      setItem.mockRestore();
    }
  });
});
