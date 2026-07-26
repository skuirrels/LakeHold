import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';

/** What is standing between this browser and a usable workbench. */
export type FirstRunMode = 'none' | 'unauthorized' | 'setup';

/** The workspace a first run asks for. */
export interface WorkspaceRequest {
  slug: string;
  displayName: string;
  catalog: string;
}

/**
 * The panel a node shows before it can be a SQL IDE.
 *
 * A fresh production deployment has no tenants and requires a credential, which used to present as
 * two empty pickers and no explanation. The three states here are the three things that can be
 * true — no credential, no workspace, and a credential just minted — and each offers the action
 * that resolves it rather than describing the problem.
 *
 * It holds the draft values and nothing else: every call to the API belongs to the parent, which
 * already owns the credential and the tenant list they change.
 */
@Component({
  selector: 'lh-first-run',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="first-run">
      <div class="card">
        @if (issuedToken(); as token) {
          <h1>Workspace ready</h1>
          <p>
            This token belongs to <strong>{{ workspace() }}</strong> and can read and write it. The
            server stored only a hash, so this is the one time it can be copied — save it somewhere
            before continuing. Scripts, BI tools, and the PostgreSQL wire endpoint all use it.
          </p>
          <pre class="token">{{ token }}</pre>
          <div class="actions">
            <button class="btn btn-primary" type="button" (click)="adoptToken.emit()">
              I have saved it — open the workspace
            </button>
          </div>
        } @else if (mode() === 'unauthorized') {
          <h1>Sign in to this Lakehold node</h1>
          <p>
            This is a private workbench. Enter the API token provided by the person who operates
            this Lakehold node.
          </p>
          <p class="operator-note">
            <strong>Setting up a new node?</strong> Lakehold writes a one-time bootstrap token to
            the API startup log. Use it here to create the first workspace and its owner credential.
          </p>
          @if (rejected()) {
            <div class="banner">
              <strong>The token this tab holds was refused</strong>
              Expired, revoked, or issued by a node whose database has since been replaced. The
              server reports all three identically, so the remedy is the same: paste a current one.
            </div>
          }
          <label class="field">
            <span>API token</span>
            <input
              type="password"
              autocomplete="off"
              placeholder="lkh_…"
              [value]="token()"
              (input)="token.set($any($event.target).value)"
              (keydown.enter)="submitToken()"
            />
          </label>
          <div class="actions">
            <button
              class="btn btn-primary"
              type="button"
              [disabled]="!token().trim()"
              (click)="submitToken()"
            >
              Sign in
            </button>
          </div>
        } @else {
          <h1>No workspaces yet</h1>
          <p>
            This node is running and empty. A workspace is a tenant, and a catalog is the isolated
            unit of data attached to your session — create the first of each to start querying.
          </p>

          <label class="field">
            <span>Workspace slug</span>
            <input
              type="text"
              autocomplete="off"
              spellcheck="false"
              placeholder="acme"
              [value]="slug()"
              (input)="slug.set($any($event.target).value)"
            />
          </label>

          <label class="field">
            <span>Display name <em>optional</em></span>
            <input
              type="text"
              autocomplete="off"
              placeholder="Acme"
              [value]="displayName()"
              (input)="displayName.set($any($event.target).value)"
            />
          </label>

          <label class="field">
            <span>Catalog name</span>
            <input
              type="text"
              autocomplete="off"
              spellcheck="false"
              [value]="catalog()"
              (input)="catalog.set($any($event.target).value)"
            />
          </label>

          @if (error(); as message) {
            <div class="banner">
              <strong>Could not create the workspace</strong>
              <pre>{{ message }}</pre>
            </div>
          }

          <div class="actions">
            <button
              class="btn btn-primary"
              type="button"
              [disabled]="busy() || !slug().trim() || !catalog().trim()"
              (click)="submitWorkspace()"
            >
              {{ busy() ? 'Creating…' : 'Create workspace' }}
            </button>
            <span class="hint">Creating a workspace needs the bootstrap credential.</span>
          </div>
        }
      </div>
    </div>
  `,
  styles: [
    `
      .first-run {
        flex: 1;
        min-height: 0;
        overflow-y: auto;
        display: flex;
        justify-content: center;
        align-items: flex-start;
        padding: 48px 20px;
      }

      .card {
        width: 100%;
        max-width: 520px;
        background: var(--surface-1);
        border: 1px solid var(--border);
        border-radius: var(--radius);
        padding: 26px 28px 24px;
      }

      h1 {
        margin: 0 0 10px;
        font-size: 19px;
        font-weight: 600;
        color: var(--text);
      }

      p {
        margin: 0 0 16px;
        font-size: 13.5px;
        line-height: 1.6;
        color: var(--text-muted);
      }

      .field {
        display: flex;
        flex-direction: column;
        gap: 5px;
        margin-bottom: 14px;
        font-size: 12px;
        color: var(--text-faint);
      }

      .field em {
        font-style: normal;
        opacity: 0.7;
      }

      input {
        width: 100%;
        box-sizing: border-box;
        padding: 8px 10px;
        font-family: var(--mono);
        font-size: 13px;
        color: var(--text);
        background: var(--surface-2);
        border: 1px solid var(--border-strong);
        border-radius: var(--radius-sm);
      }

      input:focus-visible {
        outline: 2px solid var(--accent);
        outline-offset: 1px;
      }

      .actions {
        display: flex;
        align-items: center;
        gap: 12px;
        margin-top: 18px;
        flex-wrap: wrap;
      }

      /* The buttons are the workbench's, restated because component styles do not cross the
         boundary. Only the primary variant is used here. */
      .btn {
        padding: 7px 13px;
        font: inherit;
        font-size: 13px;
        color: var(--text);
        background: var(--surface-3);
        border: 1px solid var(--border-strong);
        border-radius: var(--radius-sm);
        cursor: pointer;
      }

      .btn-primary {
        color: var(--surface-0);
        background: var(--accent);
        border-color: var(--accent);
        font-weight: 600;
      }

      .btn:disabled {
        opacity: 0.5;
        cursor: not-allowed;
      }

      .hint {
        font-size: 12px;
        color: var(--text-faint);
      }

      .token {
        margin: 0 0 16px;
        padding: 10px 12px;
        font-family: var(--mono);
        font-size: 12px;
        background: var(--surface-0);
        border: 1px solid var(--border);
        border-radius: var(--radius-sm);
        overflow-x: auto;
      }

      .operator-note {
        padding: 10px 12px;
        background: var(--surface-0);
        border: 1px solid var(--border);
        border-radius: var(--radius-sm);
      }

      .operator-note strong {
        color: var(--text);
      }

      /* Shown once and copied by hand, so it wraps rather than scrolling out of sight and is tinted
         to read as the one thing on the card that matters. */
      .token {
        white-space: pre-wrap;
        word-break: break-all;
        color: var(--accent);
        border-color: var(--border-strong);
      }

      .banner {
        margin-top: 16px;
        padding: 10px 13px;
        font-size: 13px;
        background: rgba(240, 97, 109, 0.1);
        border: 1px solid rgba(240, 97, 109, 0.4);
        border-radius: var(--radius-sm);
        color: #ffb3b9;
      }

      .banner strong {
        display: block;
        margin-bottom: 5px;
        color: var(--error);
      }

      .banner pre {
        margin: 0;
        font-family: var(--mono);
        font-size: 12px;
        white-space: pre-wrap;
        word-break: break-word;
      }
    `,
  ],
})
export class FirstRunComponent {
  /** Which panel to show. `none` means the parent renders the workbench instead. */
  readonly mode = input.required<FirstRunMode>();

  /** True while the parent is provisioning, so the button cannot be pressed twice. */
  readonly busy = input(false);

  /** A provisioning failure, shown verbatim: the API's message names what it refused and why. */
  readonly error = input<string | null>(null);

  /** Whether the credential already held was refused, as opposed to none having been offered yet. */
  readonly rejected = input(false);

  /** The freshly minted token, shown once. Its presence is what selects the final panel. */
  readonly issuedToken = input<string | null>(null);

  /** The workspace that token belongs to, for the sentence above it. */
  readonly workspace = input('');

  readonly signIn = output<string>();
  readonly createWorkspace = output<WorkspaceRequest>();
  readonly adoptToken = output<void>();

  protected readonly token = signal('');
  protected readonly slug = signal('');
  protected readonly displayName = signal('');
  protected readonly catalog = signal('analytics');

  protected submitToken(): void {
    const value = this.token().trim();
    if (value) {
      this.signIn.emit(value);
      this.token.set('');
    }
  }

  protected submitWorkspace(): void {
    const slug = this.slug().trim();
    const catalog = this.catalog().trim();
    if (!slug || !catalog || this.busy()) {
      return;
    }

    this.createWorkspace.emit({ slug, displayName: this.displayName().trim() || slug, catalog });
  }
}
