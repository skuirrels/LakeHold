import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * The failure banner a panel shows when its own request fails.
 *
 * Owned by the panel rather than by the workbench, which is what makes a stale error structurally
 * impossible: the banner is destroyed with the panel when the operator switches tabs. The previous
 * arrangement — one banner on the workbench — left a restore refusal hanging over the eject list,
 * implying the eject had failed, and had to be cleared by hand on every tab change.
 *
 * `title` names the operation. A fixed heading mislabels a restore refusal or a webhook rejection as
 * a SQL error, and the server's message is often the only other clue about where it came from.
 */
@Component({
  selector: 'lh-panel-error',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (message()) {
      <div class="banner error-banner">
        <strong>{{ title() }}</strong>
        <pre>{{ message() }}</pre>
      </div>
    }
  `,
  styles: [
    `
      /* Kept identical to the workbench's own banner: the two appear in the same column and any
         drift between them reads as two different kinds of failure. */
      .banner {
        margin: 10px 12px;
        padding: 10px 13px;
        border-radius: var(--radius-sm);
        font-size: 13px;
        flex-shrink: 0;
      }

      .error-banner {
        background: rgba(240, 97, 109, 0.1);
        border: 1px solid rgba(240, 97, 109, 0.4);
        color: #ffb3b9;
      }

      strong {
        display: block;
        margin-bottom: 5px;
        color: var(--error);
      }

      pre {
        margin: 0;
        font-family: var(--mono);
        font-size: 12px;
        white-space: pre-wrap;
        word-break: break-word;
      }
    `,
  ],
})
export class PanelErrorComponent {
  readonly title = input.required<string>();
  readonly message = input<string | null>(null);
}
