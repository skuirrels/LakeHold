/**
 * Binds `localStorage` and `sessionStorage` to the jsdom window the tests render into.
 *
 * Node 25 and later ship their own Web Storage globals. Vitest copies a jsdom window property onto
 * the global only when Node does not already define it, unless the property is on its own override
 * list, and the two storages are not. Node's `localStorage` is `undefined` without
 * `--localstorage-file`, and its `sessionStorage` is a Node `Storage` that jsdom's `Storage.prototype`
 * spies cannot reach. A Node flag would not help: Node 20 rejects it.
 */
const dom = (globalThis as { jsdom?: { window: Window } }).jsdom;

if (dom) {
  for (const name of ['localStorage', 'sessionStorage'] as const) {
    Object.defineProperty(globalThis, name, {
      configurable: true,
      get: () => dom.window[name],
    });
  }
}
