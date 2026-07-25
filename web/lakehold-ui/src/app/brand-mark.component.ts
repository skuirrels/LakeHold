import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * The Lakehold brand mark, shared by every page header so the six copies of the artwork cannot
 * drift apart. Callers keep their own `.brand` layout and `.mark` styling: the host is an inline
 * flex box, which behaves as the bare `<svg>` did as a flex child, and a `.mark` class placed on
 * the element still matches it from the surrounding page's stylesheet.
 */
@Component({
  selector: 'lh-brand-mark',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <svg
      [attr.width]="size()"
      [attr.height]="size()"
      viewBox="0 0 32 32"
      aria-hidden="true"
      xmlns="http://www.w3.org/2000/svg"
    >
      <rect width="32" height="32" rx="7" fill="#ffc857" />
      <path d="M6 10.5 Q8.5 8.3 11 10.5 Q13.5 12.7 16 10.5 Q18.5 8.3 21 10.5 Q23.5 12.7 26 10.5" stroke="#0b0f14" stroke-width="2.4" fill="none" stroke-linecap="round" />
      <path d="M6 16 Q8.5 13.8 11 16 Q13.5 18.2 16 16 Q18.5 13.8 21 16 Q23.5 18.2 26 16" stroke="#0b0f14" stroke-width="2.4" fill="none" stroke-linecap="round" />
      <path d="M6 21.5 Q8.5 19.3 11 21.5 Q13.5 23.7 16 21.5 Q18.5 19.3 21 21.5 Q23.5 23.7 26 21.5" stroke="#0b0f14" stroke-width="2.4" fill="none" stroke-linecap="round" />
    </svg>
  `,
  styles: `
    :host {
      display: inline-flex;
    }
  `,
})
export class BrandMarkComponent {
  /** Rendered edge length in pixels: 20 in the workbench top bar, 22 in the marketing headers. */
  readonly size = input(22);
}
