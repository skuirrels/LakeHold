import { formatBytes, formatCount, formatTime, quoteIdentifier, quoteTable } from './format';

describe('formatBytes', () => {
  it('reports null as an em dash rather than as zero', () => {
    // A table with no files has no average file size. Rendering that as "0 B" would claim a
    // measurement that was never taken.
    expect(formatBytes(null)).toBe('—');
    expect(formatBytes(0)).toBe('0 B');
  });

  it('uses decimal units, because DuckLake does', () => {
    // `ducklake_set_option(…, 'target_file_size', '5MB')` persists 5,000,000. Rendering that as
    // 4.8 MiB would make the storage advisory look wrong beside the target it is judged against.
    expect(formatBytes(5_000_000)).toBe('5.0 MB');
    expect(formatBytes(16_000_000)).toBe('16 MB');
  });

  it('keeps one decimal below ten and drops it above', () => {
    expect(formatBytes(1_500)).toBe('1.5 kB');
    expect(formatBytes(43_000)).toBe('43 kB');
    expect(formatBytes(3_413_989)).toBe('3.4 MB');
  });

  it('stays in bytes below a kilobyte', () => {
    expect(formatBytes(999)).toBe('999 B');
    expect(formatBytes(1_000)).toBe('1.0 kB');
  });

  it('climbs through the units without running off the end', () => {
    expect(formatBytes(2_500_000_000)).toBe('2.5 GB');
    expect(formatBytes(4_000_000_000_000)).toBe('4.0 TB');
    expect(formatBytes(9_000_000_000_000_000)).toBe('9.0 PB');
  });
});

describe('formatCount', () => {
  it('groups thousands so magnitudes are readable at a glance', () => {
    expect(formatCount(250_000)).toBe((250_000).toLocaleString());
    expect(formatCount(0)).toBe('0');
  });
});

describe('formatTime', () => {
  it('stays terse for today', () => {
    const midMorning = new Date();
    midMorning.setHours(9, 30, 0, 0);

    expect(formatTime(midMorning.toISOString())).toBe(midMorning.toLocaleTimeString());
  });

  it('adds the date once it is not today', () => {
    // The panels this serves — backups, ejects, snapshots, the scheduled-run log — all span days.
    // "Did last night's backup run" is unanswerable if yesterday and today read identically.
    const yesterday = new Date();
    yesterday.setDate(yesterday.getDate() - 1);

    const rendered = formatTime(yesterday.toISOString());
    expect(rendered).not.toBe(yesterday.toLocaleTimeString());
    expect(rendered).toContain(yesterday.toLocaleTimeString());
  });

  it('distinguishes the same clock time on two different days', () => {
    const now = new Date();
    const lastWeek = new Date(now);
    lastWeek.setDate(lastWeek.getDate() - 7);

    expect(formatTime(lastWeek.toISOString())).not.toBe(formatTime(now.toISOString()));
  });
});

describe('SQL identifier quoting', () => {
  it('quotes awkward catalog names rather than rejecting them', () => {
    expect(quoteTable('warm-zone', 'order.items')).toBe('"warm-zone"."order.items"');
  });

  it('escapes embedded quotes using the SQL-standard doubled form', () => {
    expect(quoteIdentifier('a"b')).toBe('"a""b"');
  });
});
