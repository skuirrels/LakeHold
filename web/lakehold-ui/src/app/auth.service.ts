import { Injectable, computed, signal } from '@angular/core';

/**
 * Holds the API credential the workbench presents.
 *
 * Where it lives is a choice the person signing in makes, because the two answers protect different
 * things and neither is right for everyone:
 *
 *   * `sessionStorage` — the default — is discarded when the tab closes, which bounds how long a
 *     stolen token stays useful. On a node with no identity provider it also means pasting the
 *     credential again after every tab close, and friction that constant is worked around by
 *     keeping the token somewhere worse than a browser.
 *   * `localStorage` survives, which is the only thing "keep me signed in" can mean, at the cost of
 *     a long-lived bearer sitting in browser storage.
 *
 * The default stays session-scoped, so the safer behaviour is what you get without asking for it.
 * This is the machine and break-glass path either way. Interactive humans use the same-origin OIDC
 * session, where JavaScript never receives the identity-provider token at all.
 *
 * API authentication is unconditional. A missing token is useful only when the deployment has
 * deliberately configured its scoped demo-reader identity; the stored-token path remains the
 * machine and break-glass alternative to an OIDC browser session.
 */
const STORAGE_KEY = 'lakehold.token';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly restored = readStored();
  private readonly _token = signal<string | null>(this.restored.token);
  private readonly _persistent = signal(this.restored.persistent);

  /** The current bearer token, or null when none is set. */
  readonly token = this._token.asReadonly();

  /** Whether a credential is currently held. */
  readonly hasToken = computed(() => (this._token() ?? '').length > 0);

  /** Whether the held credential survives closing the tab. */
  readonly persistent = this._persistent.asReadonly();

  /**
   * Stores a token, trimming it; a blank value clears instead.
   *
   * @param persist Keep it across tab closes. Defaults to false, so a caller that has not made the
   *   choice explicitly gets the session-scoped behaviour rather than the durable one.
   */
  setToken(token: string, persist = false): void {
    const trimmed = token.trim();
    if (trimmed.length === 0) {
      this.clear();
      return;
    }

    this._token.set(trimmed);
    this._persistent.set(persist);

    // Written to one store and removed from the other, never left in both. A token still sitting in
    // localStorage after the choice moved back to session-scoped would outlive the tab it was
    // scoped to, which is the whole guarantee the default exists to make.
    if (persist) {
      write(() => localStorage, trimmed);
      remove(() => sessionStorage);
    } else {
      write(() => sessionStorage, trimmed);
      remove(() => localStorage);
    }
  }

  /** Forgets the token from both stores. Subsequent requests are anonymous again. */
  clear(): void {
    this._token.set(null);
    this._persistent.set(false);
    remove(() => sessionStorage);
    remove(() => localStorage);
  }
}

function write(store: () => Storage, value: string): void {
  try {
    store().setItem(STORAGE_KEY, value);
  } catch {
    // A browser with storage disabled keeps the token in memory for the session; that is enough.
  }
}

function remove(store: () => Storage): void {
  try {
    store().removeItem(STORAGE_KEY);
  } catch {
    // Nothing to remove if storage is unavailable.
  }
}

function read(store: () => Storage): string | null {
  try {
    return store().getItem(STORAGE_KEY);
  } catch {
    return null;
  }
}

/**
 * Recovers the credential and where it was kept.
 *
 * localStorage is read first, so a deliberate "keep me signed in" wins over a session entry left by
 * an earlier visit in the same tab.
 */
function readStored(): { token: string | null; persistent: boolean } {
  const persisted = read(() => localStorage);
  if (persisted) {
    return { token: persisted, persistent: true };
  }

  return { token: read(() => sessionStorage), persistent: false };
}
