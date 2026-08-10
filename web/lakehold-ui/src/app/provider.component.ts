import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { BrandMarkComponent } from './brand-mark.component';
import { ThemeToggleComponent } from './theme-toggle.component';

const PROVIDER_VERSIONS_URL =
  'https://api.nuget.org/v3-flatcontainer/duckdb.efcoreprovider/index.json';

interface NugetVersionIndex {
  readonly versions?: unknown;
}

/** Returns the newest stable normalized package version, independent of index ordering. */
export function latestStableProviderVersion(index: unknown): string | null {
  if (typeof index !== 'object' || index === null) {
    return null;
  }

  const versions = (index as NugetVersionIndex).versions;
  if (!Array.isArray(versions)) {
    return null;
  }

  let latest: { version: string; parts: readonly number[] } | null = null;
  for (const value of versions) {
    if (typeof value !== 'string') {
      continue;
    }

    const version = value.trim();
    if (!/^\d+\.\d+\.\d+(?:\.\d+)?$/.test(version)) {
      continue;
    }

    const parts = version.split('.').map(Number);
    if (!parts.every(Number.isSafeInteger)) {
      continue;
    }

    if (latest === null || compareVersionParts(parts, latest.parts) > 0) {
      latest = { version, parts };
    }
  }

  return latest?.version ?? null;
}

function compareVersionParts(left: readonly number[], right: readonly number[]): number {
  for (let index = 0; index < Math.max(left.length, right.length); index++) {
    const difference = (left[index] ?? 0) - (right[index] ?? 0);
    if (difference !== 0) {
      return difference;
    }
  }
  return 0;
}

/**
 * The DuckDB.EFCoreProvider surface: the provider's value proposition.
 *
 * The provider is a separate package with its own repository, but it is the data plane LakeHold is
 * built on, so its documentation is served from this site rather than from a second one. This page
 * is the pitch only — the reference prose is `ProviderDocsComponent` at `/provider/docs`, kept apart
 * so that a "Docs" link never has to mean the provider's documentation on one page and LakeHold's on
 * the next.
 */
@Component({
  selector: 'lh-provider',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandMarkComponent, RouterLink, ThemeToggleComponent],
  templateUrl: './provider.component.html',
  styleUrls: ['./site-header.css', './provider.component.css'],
})
export class ProviderComponent {
  private readonly providerVersion = signal('Latest');

  protected readonly stats = computed(() => [
    { value: 'EF Core 10', label: 'Native provider, .NET 10' },
    { value: this.providerVersion(), label: 'Current release on NuGet' },
    { value: 'S3 · GCS · Azure', label: 'Verified archive paths' },
    { value: 'MIT', label: 'Licence, open source' },
  ]);

  constructor() {
    // Keep the prerender truthful without making the build depend on NuGet. The browser replaces
    // "Latest" only after NuGet returns a valid stable version, so an outage cannot leave a stale
    // release number behind or prevent the provider documentation from rendering.
    afterNextRender(() => void this.loadLatestProviderVersion());
  }

  private async loadLatestProviderVersion(): Promise<void> {
    try {
      const response = await globalThis.fetch(PROVIDER_VERSIONS_URL, { credentials: 'omit' });
      if (!response.ok) {
        return;
      }

      const latest = latestStableProviderVersion(await response.json());
      if (latest !== null) {
        this.providerVersion.set(`v${latest}`);
      }
    } catch {
      // NuGet is an enhancement for this one display value, not a page availability dependency.
    }
  }

  protected readonly pillars = [
    {
      label: '01 — Native',
      title: 'Write familiar LINQ, get analytical SQL',
      body: 'Joins, grouping, ordering, aggregates, string, math, temporal, arrays, JSON traversal, and relational composition — all translated for DuckDB.',
    },
    {
      label: '02 — Fast path',
      title: 'Load at columnar speed',
      body: 'Appender-backed BulkInsert reaches roughly one million rows per second, and primary-key Upsert stages an appender batch into a set-based merge.',
    },
    {
      label: '03 — File native',
      title: 'Query files like tables',
      body: 'Map entities directly onto Parquet, CSV, or JSON — local or on object storage. Keep the data where it is and compose over it with LINQ.',
    },
    {
      label: '04 — Complete',
      title: 'Reads and writes, no asterisk',
      body: 'SaveChanges, transactions, migrations, optimistic concurrency, scaffolding, and store-generated keys, on the native DuckDB profile.',
    },
    {
      label: '05 — Toolable',
      title: 'Translate without executing',
      body: 'Capture exact SQL and parameters for queries and terminal aggregates, replay named commands unchanged, and inspect store-type support through public APIs.',
    },
  ];

  protected readonly profiles = [
    {
      label: 'A — Embedded',
      title: 'DuckDB file',
      body: 'A complete relational provider over a local file: tracking, transactions, migrations, scaffolding, and analytical LINQ.',
      code: 'options.UseDuckDB("Data Source=app.duckdb")',
      featured: false,
    },
    {
      label: 'B — Tiered',
      title: 'DuckDB + Parquet archive',
      body: 'Keep the working set hot and writable; archive complete relational aggregates to hive-partitioned Parquet on disk or object storage.',
      code: 'modelBuilder.ToTieredStore<Record>(…)',
      featured: true,
    },
    {
      label: 'C — Lakehouse',
      title: 'DuckLake catalog',
      body: 'Tracked writes, commit metadata, named-secret shares, and independent read-only contexts against a shared catalog. This is the profile LakeHold uses.',
      code: 'options.UseDuckLake("catalog/analytics.ducklake")',
      featured: false,
    },
  ];

  protected readonly lifecycle = [
    { name: 'Archive', body: 'move aggregate windows' },
    { name: 'Reconcile', body: 'publish late corrections' },
    { name: 'Restore', body: 'bring history back hot' },
    { name: 'Compact', body: 'rewrite full generations' },
    { name: 'Inspect', body: 'inventory + preflight' },
    { name: 'Evolve', body: 'verified contract rewrites' },
  ];

  /**
   * Bar widths are relative to the slower path rather than proportional to the times: at ~140× the
   * faster bar would be a hairline, which reads as a rendering fault rather than as a result.
   */
  protected readonly benchmark = [
    { name: 'SaveChanges', note: 'Full EF semantics', value: '~12.5 s', width: '100%' },
    { name: 'BulkInsert', note: 'Appender fast path', value: '~90 ms', width: '8%' },
  ];

  /**
   * Deep links into `/provider/docs`. The ids are the heading slugs `renderMarkdown` derives from
   * `provider.content.md`, so a renamed heading has to be renamed here too.
   */
  protected readonly docLinks = [
    { id: 'install', label: 'Install' },
    { id: 'choose-a-backend', label: 'Choose a backend' },
    { id: 'model-and-query', label: 'Model and query' },
    { id: 'query-command-plans', label: 'Query command plans' },
    { id: 'write-paths', label: 'Write paths' },
    { id: 'file-analytics', label: 'File analytics' },
    { id: 'ducklake', label: 'DuckLake' },
    { id: 'tiered-storage', label: 'Tiered storage' },
    { id: 'compatibility', label: 'Compatibility' },
    { id: 'reference', label: 'Reference' },
  ];
}
