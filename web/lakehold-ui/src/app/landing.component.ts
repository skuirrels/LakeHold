import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';

/** Marketing surface: the value proposition, stated with its trade-offs. */
@Component({
  selector: 'lh-landing',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <div class="landing">
      <header class="nav">
        <div class="brand"><span class="mark">◆</span> Lakehold</div>
        <nav class="nav-links">
          <a routerLink="/compare">Compare</a>
          <a class="btn btn-primary" routerLink="/workbench">Open workbench →</a>
        </nav>
      </header>

      <section class="hero">
        <span class="eyebrow">Open-source lakehouse · DuckDB + DuckLake · .NET</span>
        <h1>Your lakehouse.<br />Your bucket. Your VPC.</h1>
        <p class="lede">
          A serverless-feeling DuckDB warehouse that runs on <em>your</em> infrastructure, stores every
          byte as open Parquet you can read without us, and speaks .NET natively.
        </p>
        <div class="cta">
          <a class="btn btn-primary lg" routerLink="/workbench">Open the workbench</a>
          <a class="btn lg" routerLink="/compare">How we compare</a>
        </div>
      </section>

      <section class="pillars">
        @for (pillar of pillars; track pillar.title) {
          <article class="pillar">
            <div class="pillar-icon">{{ pillar.icon }}</div>
            <h2>{{ pillar.title }}</h2>
            <p>{{ pillar.body }}</p>
          </article>
        }
      </section>

      <section class="proof">
        <h2 class="section-title">Verified, not asserted</h2>
        <p class="section-sub">
          Every number below came from running the stack, not from a datasheet.
        </p>
        <div class="stats">
          @for (stat of stats; track stat.label) {
            <div class="stat">
              <div class="stat-value">{{ stat.value }}</div>
              <div class="stat-label">{{ stat.label }}</div>
            </div>
          }
        </div>
      </section>

      <section class="whatsnew">
        <h2 class="section-title">Recently shipped</h2>
        <p class="section-sub">
          July 2026 — durability. The metadata catalog was the one part of a DuckLake deployment that
          was not already an open format. It is now.
        </p>

        <ol class="changelog">
          @for (entry of changelog; track entry.title) {
            <li class="entry">
              <span class="tag">{{ entry.tag }}</span>
              <div class="entry-body">
                <h3>{{ entry.title }}</h3>
                <p>{{ entry.body }}</p>
                @if (entry.caveat) {
                  <p class="caveat"><strong>Caveat:</strong> {{ entry.caveat }}</p>
                }
              </div>
            </li>
          }
        </ol>
      </section>

      <section class="compare">
        <h2 class="section-title">Where we win, and where we don't</h2>
        <p class="section-sub">
          A comparison that only listed our strengths would be marketing, not engineering.
        </p>

        <div class="compare-grid">
          <div class="col win">
            <h3>Choose Lakehold when</h3>
            <ul>
              <li>Data residency or a security review rules out a hosted warehouse.</li>
              <li>You want your tables as open Parquet in a bucket you control.</li>
              <li>Your stack is .NET and you want EF Core and analytics on one model.</li>
              <li>You would rather pay for a VM than per-second compute.</li>
              <li>You need explicit control over compaction, retention, and snapshots.</li>
            </ul>
          </div>
          <div class="col lose">
            <h3>Choose MotherDuck when</h3>
            <ul>
              <li>You want zero operations and no infrastructure to own.</li>
              <li>You need elastic scale-out beyond a single node.</li>
              <li>Hybrid local-and-cloud dual execution matters to you.</li>
              <li>You want managed ingestion connectors out of the box.</li>
              <li>Your team is Python-first and wants the most mature UI today.</li>
            </ul>
          </div>
        </div>

        <p class="compare-more">
          <a routerLink="/compare">Full comparison — MotherDuck, ClickHouse, and the cloud warehouses →</a>
        </p>
      </section>

      <footer class="foot">
        <p class="domain"><a href="https://lakehold.dev">lakehold.dev</a></p>
        <p>
          Built on <a href="https://duckdb.org" target="_blank" rel="noopener">DuckDB</a>,
          <a href="https://ducklake.select" target="_blank" rel="noopener">DuckLake</a>, .NET 10, and Angular.
          Apache-2.0.
        </p>
      </footer>
    </div>
  `,
  styleUrl: './landing.component.css',
})
export class LandingComponent {
  protected readonly pillars = [
    {
      icon: '🔒',
      title: 'Self-hosted by default',
      body: 'Runs on a laptop, a VM, Kubernetes, or an air-gapped network. There is no vendor control plane in the request path and no egress to anyone else’s account.',
    },
    {
      icon: '📂',
      title: 'Open format, real exit path',
      body: 'DuckLake stores tables as plain Parquet and metadata as ordinary SQL. Delete Lakehold tomorrow and read_parquet() still works from Spark, Polars, or DuckDB.',
    },
    {
      icon: '⚡',
      title: '.NET-native',
      body: 'Through the EF Core provider, your application model and your lakehouse tables are the same model. No separate transformation layer, no schema drift.',
    },
    {
      icon: '🧭',
      title: 'Operator controls',
      body: 'Compaction, snapshot expiry, inlined-data flush, catalog backup, and orphan cleanup are first-class operations rather than hidden behind a managed service — on a schedule you set, with the destructive ones dry-run by default.',
    },
  ];

  /**
   * Each entry carries its own caveat where one exists. A changelog that only lists what now works
   * is an announcement; the limits are the part an operator actually needs before they rely on it.
   */
  protected readonly changelog = [
    {
      tag: 'Durability',
      title: 'Catalog backup and restore',
      body: 'The metadata catalog is exported to Parquet hourly and can rebuild a working catalog from that export — row counts, deletions, updated values, views, and AT (VERSION => n) time travel all intact. Restore writes a new catalog and refuses to overwrite an existing one, because recovery happens under pressure.',
      caveat:
        'An export with no completion manifest is refused outright. If it died partway and the missing table is ducklake_delete_file, deleted rows would silently come back.',
    },
    {
      tag: 'Portability',
      title: 'PostgreSQL is no longer a lock-in point',
      body: 'A catalog whose metadata lives in PostgreSQL exports the same 30 tables and restores into a plain DuckDB file — verified against PostgreSQL 17. That makes it an exit path from the catalog database, not just a copy of it. pg_dump restores into PostgreSQL; this restores into a file you can open with the duckdb CLI and nothing else.',
      caveat: null,
    },
    {
      tag: 'Storage',
      title: 'Backups can live in your bucket',
      body: 'The backup root can be an s3:// prefix. Listing generations, reading manifests, and restoring from a bucket are all verified against a live S3 endpoint.',
      caveat:
        'Retention cannot prune a bucket — DuckDB has no delete for object stores. Set a lifecycle rule on the prefix. Lakehold reports "retention deferred" rather than a "0 pruned" that would read as though it had run.',
    },
    {
      tag: 'Operations',
      title: 'Scheduled maintenance, safe on more than one node',
      body: 'Flush, backup, and compact run on cron schedules you control, with recent runs and their timings readable over the API. Where a catalog can genuinely be shared between nodes, a lease stops every node running the same sweep.',
      caveat:
        'Snapshot expiry and orphan cleanup are deliberately not scheduled. They are irreversible, so they stay manual and dry-run by default.',
    },
  ];

  protected readonly stats = [
    { value: '23 ms', label: 'Aggregate over 250k rows' },
    { value: '250k', label: 'Rows read back from bare Parquet' },
    { value: '30', label: 'Metadata tables backed up and restored' },
    { value: '11', label: 'Automated tests, 5 against live services' },
    { value: '0', label: 'Vendor services in the query path' },
    { value: 'Apache-2.0', label: 'Licence, no open-core catch' },
  ];
}
