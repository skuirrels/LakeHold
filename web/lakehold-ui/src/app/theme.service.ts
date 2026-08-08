import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import { Injectable, PLATFORM_ID, computed, inject, signal } from '@angular/core';

/** The two palettes `styles.css` defines. */
export type Theme = 'dark' | 'light';

/** Storage key shared with the pre-paint script in `index.html`; changing one changes both. */
export const THEME_STORAGE_KEY = 'lakehold.theme';

/**
 * The palette the app is not explicitly asked for.
 *
 * Dark, deliberately, rather than the operating system's preference: dark is the palette this
 * product shipped with and the one its screenshots and documentation describe, so following the OS
 * would silently repaint every existing install. Light is opt-in, and the choice is remembered.
 */
export const DEFAULT_THEME: Theme = 'dark';

/**
 * Owns the active palette.
 *
 * The palette is a single `data-theme` attribute on the document element, which `styles.css` keys
 * its light token block off. Everything visual therefore follows from one attribute rather than
 * from components knowing which theme they are drawn in.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly document = inject(DOCUMENT);
  private readonly platformId = inject(PLATFORM_ID);

  private readonly current = signal<Theme>(DEFAULT_THEME);

  /** The active palette. */
  readonly theme = this.current.asReadonly();

  /** Convenience for templates that only need to know which icon to draw. */
  readonly isLight = computed(() => this.current() === 'light');

  constructor() {
    // Prerendering has no storage and no user to have chosen, so the server always emits the
    // default palette and the browser reconciles on boot.
    if (isPlatformBrowser(this.platformId)) {
      this.set(this.restore());
    }
  }

  /** Switches to the other palette and remembers the choice. */
  toggle(): void {
    this.set(this.current() === 'light' ? 'dark' : 'light');
  }

  /** Applies a palette, remembering it for the next visit. */
  set(theme: Theme): void {
    this.current.set(theme);
    this.document.documentElement.setAttribute('data-theme', theme);
    this.persist(theme);
  }

  private restore(): Theme {
    // A browser can refuse storage entirely (Safari private browsing throws on access, not on
    // read), and a stored value can be anything if something else wrote the key.
    try {
      const stored = this.document.defaultView?.localStorage.getItem(THEME_STORAGE_KEY);
      return stored === 'light' || stored === 'dark' ? stored : DEFAULT_THEME;
    } catch {
      return DEFAULT_THEME;
    }
  }

  private persist(theme: Theme): void {
    if (!isPlatformBrowser(this.platformId)) {
      return;
    }

    try {
      this.document.defaultView?.localStorage.setItem(THEME_STORAGE_KEY, theme);
    } catch {
      // A palette that cannot be remembered is still worth applying for this tab.
    }
  }
}
