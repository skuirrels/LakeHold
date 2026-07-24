import { Injectable, computed, signal } from '@angular/core';

/**
 * Holds the API credential the workbench presents.
 *
 * The token lives in `sessionStorage`, not `localStorage`: it is cleared when the tab closes, which
 * narrows the window a stolen token is usable rather than persisting it indefinitely. This is still
 * the interim answer for machines and single-operator installs — the durable answer for humans is
 * OIDC (see `docs/AUTHENTICATION.md`), where the browser never holds a long-lived bearer at all.
 *
 * While the API leaves authentication optional, no token is needed and the workbench works exactly
 * as before; setting one is what lets it keep working once a deployment requires authentication.
 */
const STORAGE_KEY = 'lakehold.token';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly _token = signal<string | null>(readStored());

  /** The current bearer token, or null when none is set. */
  readonly token = this._token.asReadonly();

  /** Whether a credential is currently held. */
  readonly hasToken = computed(() => (this._token() ?? '').length > 0);

  /** Stores a token, trimming it; a blank value clears instead. */
  setToken(token: string): void {
    const trimmed = token.trim();
    if (trimmed.length === 0) {
      this.clear();
      return;
    }

    this._token.set(trimmed);
    try {
      sessionStorage.setItem(STORAGE_KEY, trimmed);
    } catch {
      // A browser with storage disabled keeps the token in memory for the session; that is enough.
    }
  }

  /** Forgets the token. Subsequent requests are anonymous again. */
  clear(): void {
    this._token.set(null);
    try {
      sessionStorage.removeItem(STORAGE_KEY);
    } catch {
      // Nothing to remove if storage is unavailable.
    }
  }
}

function readStored(): string | null {
  try {
    return sessionStorage.getItem(STORAGE_KEY);
  } catch {
    return null;
  }
}
