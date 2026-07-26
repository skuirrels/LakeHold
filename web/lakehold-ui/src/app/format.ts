/**
 * Display formatting shared by the workbench panels.
 *
 * Plain functions rather than a service or a pipe: they hold no state and take no dependencies, and
 * every panel needs the same three. A pipe would be the Angular-idiomatic choice for a template-only
 * helper, but these are also called from component code.
 */

/**
 * Renders a byte count at human scale.
 *
 * Decimal units, not binary: DuckLake's own `target_file_size` accepts and stores `'5MB'` as
 * 5,000,000 bytes, so showing 4.8 MiB beside a 5 MB target would make the storage advisory look
 * wrong when it is right.
 */
export function formatBytes(bytes: number | null): string {
  if (bytes === null) {
    return '—';
  }
  if (bytes < 1000) {
    return `${bytes} B`;
  }

  const units = ['kB', 'MB', 'GB', 'TB', 'PB'];
  let value = bytes / 1000;
  let unit = 0;
  while (value >= 1000 && unit < units.length - 1) {
    value /= 1000;
    unit++;
  }

  return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[unit]}`;
}

export function formatCount(value: number): string {
  return value.toLocaleString();
}

/**
 * Renders a timestamp, adding the date once it is no longer today's.
 *
 * A bare clock time was fine when this only labelled query history from the current session. It is
 * wrong for the panels that came later: backup generations, eject bundles, snapshots and the
 * scheduled-run log all span days, and "did last night's backup run" cannot be answered by a reading
 * that makes yesterday and today look identical. Today stays terse because that is the common case.
 */
export function formatTime(iso: string): string {
  const at = new Date(iso);
  const now = new Date();
  const sameDay =
    at.getFullYear() === now.getFullYear() &&
    at.getMonth() === now.getMonth() &&
    at.getDate() === now.getDate();

  return sameDay
    ? at.toLocaleTimeString()
    : `${at.toLocaleDateString(undefined, { day: 'numeric', month: 'short' })} ${at.toLocaleTimeString()}`;
}

/**
 * Splits a `schema.table` label back into its parts.
 *
 * Only the *first* dot separates: a table may legitimately contain one, and DuckLake happily stores
 * `main.my.table`. Splitting on the last dot, or on every dot, would address the wrong table.
 */
export function splitQualified(qualified: string): [schema: string, table: string] {
  const cut = qualified.indexOf('.');
  return cut === -1 ? ['main', qualified] : [qualified.slice(0, cut), qualified.slice(cut + 1)];
}
