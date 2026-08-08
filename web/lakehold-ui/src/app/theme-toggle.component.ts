import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ThemeService } from './theme.service';

/**
 * The light/dark switch that sits at the right-hand end of every header.
 *
 * The icon shows the palette the button *moves to* rather than the one in force — a sun while the
 * app is dark. That is the convention every browser and editor uses, and the label says the same
 * thing in words so the icon is never the only cue.
 */
@Component({
  selector: 'lh-theme-toggle',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      class="theme-toggle"
      type="button"
      [attr.aria-label]="label()"
      [attr.aria-pressed]="theme.isLight()"
      [title]="label()"
      (click)="theme.toggle()"
    >
      @if (theme.isLight()) {
        <svg viewBox="0 0 20 20" aria-hidden="true">
          <path d="M16.5 12.4A7 7 0 0 1 7.6 3.5a7 7 0 1 0 8.9 8.9Z" />
        </svg>
      } @else {
        <svg viewBox="0 0 20 20" aria-hidden="true">
          <circle cx="10" cy="10" r="3.6" />
          <path d="M10 1.6v2M10 16.4v2M1.6 10h2M16.4 10h2M4.1 4.1l1.4 1.4M14.5 14.5l1.4 1.4M15.9 4.1l-1.4 1.4M5.5 14.5l-1.4 1.4" />
        </svg>
      }
    </button>
  `,
  styles: `
    :host {
      display: inline-flex;
    }

    .theme-toggle {
      width: 30px;
      height: 30px;
      display: inline-grid;
      place-items: center;
      flex-shrink: 0;
      color: var(--text-muted);
      background: none;
      border: 1px solid transparent;
      border-radius: var(--radius-sm);
      transition: color 0.12s ease, background 0.12s ease, border-color 0.12s ease;
    }

    .theme-toggle:hover {
      color: var(--text);
      background: var(--surface-2);
      border-color: var(--border);
    }

    .theme-toggle svg {
      width: 17px;
      height: 17px;
      fill: none;
      stroke: currentColor;
      stroke-width: 1.6;
      stroke-linecap: round;
      stroke-linejoin: round;
    }
  `,
})
export class ThemeToggleComponent {
  protected readonly theme = inject(ThemeService);

  protected readonly label = computed(() =>
    this.theme.isLight() ? 'Switch to dark theme' : 'Switch to light theme',
  );
}
