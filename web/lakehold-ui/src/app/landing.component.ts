import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BrandMarkComponent } from './brand-mark.component';
import { ThemeToggleComponent } from './theme-toggle.component';

/** Marketing surface: the value proposition, stated with its trade-offs. */
@Component({
  selector: 'lh-landing',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandMarkComponent, RouterLink, ThemeToggleComponent],
  template: `
    <!--
      The header sits outside \`.landing\` so the sticky bar's surface reaches the viewport edges.
      Inside the padded, max-width column it would have stopped short of them and read as a floating
      panel rather than page chrome; \`.nav-inner\` re-applies that column to the links themselves.
    -->
    <header class="nav">
      <div class="nav-inner">
        <div class="brand">
          <lh-brand-mark class="mark" />
          LakeHold
        </div>
        <nav class="nav-links">
          <a routerLink="/enterprise-data-platform">Enterprise Platform</a>
          <a routerLink="/docs">Docs</a>
          <a class="provider" routerLink="/provider">Provider</a>
          <a routerLink="/compare">Compare</a>
          <a
            class="icon-link"
            href="https://github.com/skuirrels/LakeHold"
            target="_blank"
            rel="noopener noreferrer"
            aria-label="LakeHold on GitHub"
            title="LakeHold on GitHub"
          >
            <svg
              width="20"
              height="20"
              viewBox="0 0 16 16"
              fill="currentColor"
              aria-hidden="true"
              xmlns="http://www.w3.org/2000/svg"
            >
              <path
                d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82a7.42 7.42 0 0 1 2-.27c.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.01 8.01 0 0 0 16 8c0-4.42-3.58-8-8-8Z"
              />
            </svg>
          </a>
          <a class="btn btn-primary nav-workbench" routerLink="/workbench">Open workbench →</a>
          <lh-theme-toggle />
        </nav>
      </div>
    </header>

    <div class="landing">
      <section class="hero">
        <div class="hero-copy">
          <span class="eyebrow"
            >Open-source lakehouse · PostgreSQL + DuckDB + DuckLake · Java · .NET · Go ·
            Python</span
          >
          <!--
            The product name belongs in the heading and in the sentence below it. Without it the
            only page on the site that *states* what LakeHold is was the documentation, which is how
            a getting-started guide comes to answer a search for the product's own name.
          -->
          <h1>LakeHold: an Enterprise LakeHouse, you host yourself</h1>
          <p class="lede">
            LakeHold is self-hostable, tenant-aware, and built on open Parquet — governed data
            infrastructure that stays on <em>your</em> infrastructure.
          </p>
          <div class="cta hero-actions">
            <a class="btn btn-primary lg" routerLink="/workbench">Open the workbench</a>
            <a class="btn lg" routerLink="/docs">Get started</a>
          </div>
        </div>

        <div
          class="topology"
          role="img"
          aria-label="LakeHold connectivity map: PostgreSQL, Kafka with Avro, and REST or gRPC feed governed LakeHold; people connect through enterprise SSO, agents connect through the MCP server, and consumers use SQL, Java, .NET, Go, Python, or open Parquet."
        >
          <div class="topology-grid" aria-hidden="true"></div>
          <span class="topology-label sources-label">Sources</span>
          <span class="topology-label outputs-label">Outputs</span>

          <svg
            class="topology-lines"
            viewBox="0 0 760 470"
            preserveAspectRatio="none"
            aria-hidden="true"
          >
            <defs>
              <filter id="flow-glow" x="-20%" y="-20%" width="140%" height="140%">
                <feGaussianBlur stdDeviation="2.5" result="blur" />
                <feMerge>
                  <feMergeNode in="blur" />
                  <feMergeNode in="SourceGraphic" />
                </feMerge>
              </filter>
            </defs>
            <g class="ingest-lines" filter="url(#flow-glow)">
              <path d="M190 145 H250 Q280 145 280 178 V222 H338" />
              <path d="M190 235 H338" />
              <path d="M190 325 H250 Q280 325 280 292 V250 H338" />
            </g>
            <g class="serve-lines" filter="url(#flow-glow)">
              <path d="M490 220 H526 Q550 220 550 176 V155 H590" />
              <path d="M490 238 H590" />
              <path d="M490 256 H526 Q550 256 550 302 V324 H590" />
            </g>
            <g class="access-lines">
              <path d="M366 100 V118 H382" />
              <path d="M508 100 V118 H472" />
              <path d="M405 326 V353 H372 V383" />
            </g>
          </svg>

          <div class="topology-node access-node sso-node">
            <svg viewBox="0 0 24 24" aria-hidden="true">
              <path d="M12 2.5 20 6v5.7c0 4.7-3.2 8.4-8 10.3-4.8-1.9-8-5.6-8-10.3V6l8-3.5Z" />
              <path d="M12 7v8M9 11h6" />
            </svg>
            <span><strong>Enterprise SSO</strong><small>OIDC identity provider</small></span>
          </div>
          <div class="topology-node access-node mcp-node">
            <svg viewBox="0 0 24 24" aria-hidden="true">
              <circle cx="6" cy="6" r="2.5" />
              <circle cx="18" cy="6" r="2.5" />
              <circle cx="18" cy="18" r="2.5" />
              <path d="M8.5 6h4A3.5 3.5 0 0 1 16 9.5V18M6 8.5V16a2 2 0 0 0 2 2h7.5" />
            </svg>
            <span><strong>MCP Server</strong><small>OAuth-secured agent access</small></span>
          </div>

          <div class="source-stack">
            <div class="topology-node">
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <ellipse cx="12" cy="5" rx="7" ry="3" />
                <path d="M5 5v7c0 1.7 3.1 3 7 3s7-1.3 7-3V5M5 12v7c0 1.7 3.1 3 7 3s7-1.3 7-3v-7" />
              </svg>
              <span><strong>PostgreSQL</strong><small>Incremental connector</small></span>
            </div>
            <div class="topology-node">
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <circle cx="6" cy="5" r="2.5" />
                <circle cx="18" cy="12" r="2.5" />
                <circle cx="6" cy="19" r="2.5" />
                <path d="m8.2 6.2 7.6 4.6M8.2 17.8l7.6-4.6M6 7.5v9" />
              </svg>
              <span><strong>Kafka + Avro</strong><small>Registry-backed events</small></span>
            </div>
            <div class="topology-node">
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <path d="M7.2 18.5H18a4 4 0 0 0 .5-8A6.5 6.5 0 0 0 6 9a4.8 4.8 0 0 0 1.2 9.5Z" />
              </svg>
              <span><strong>REST / gRPC</strong><small>Managed snapshots</small></span>
            </div>
          </div>

          <div class="lakehold-node">
            <lh-brand-mark [size]="58" />
            <strong>Governed LakeHold</strong>
            <small>DuckDB + DuckLake</small>
            <span class="node-status"><i></i> Snapshot current</span>
          </div>

          <div class="output-stack">
            <div class="topology-node">
              <span class="protocol-icon">SQL</span>
              <span><strong>SQL</strong><small>PostgreSQL endpoint</small></span>
            </div>
            <div class="topology-node">
              <span class="protocol-icon">API</span>
              <span
                ><strong>Java · .NET · Go · Python</strong
                ><small>First-party source SDKs</small></span
              >
            </div>
            <div class="topology-node">
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <path d="M6 2.5h8l4 4V22H6zM14 2.5v5h4M9 13h6M9 17h6" />
              </svg>
              <span><strong>Parquet</strong><small>Open table storage</small></span>
            </div>
          </div>

          <div class="control-plane">
            <span class="control-plane-title">Control plane</span>
            <div>
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <path d="M6 4h12v16H6zM9 8h6M9 12h6M9 16h4" />
              </svg>
              <strong>Catalog</strong><small>Schemas &amp; tables</small>
            </div>
            <div>
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <circle cx="9" cy="8" r="3" />
                <path
                  d="M3.5 20v-2.5A4.5 4.5 0 0 1 8 13h2a4.5 4.5 0 0 1 4.5 4.5V20M16 11h5v7h-5zM17.5 11V9.5a1 1 0 0 1 2 0V11"
                />
              </svg>
              <strong>Tenant access</strong><small>SSO · roles · memberships</small>
            </div>
            <div>
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <circle cx="6" cy="6" r="2.5" />
                <circle cx="18" cy="6" r="2.5" />
                <circle cx="6" cy="18" r="2.5" />
                <circle cx="18" cy="18" r="2.5" />
                <path d="M8.5 6h7M6 8.5v7M18 8.5v7M8.5 18h7" />
              </svg>
              <strong>Connector runs</strong><small>Schedules &amp; evidence</small>
            </div>
            <div>
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <path d="M6 3h9l3 3v15H6zM14 3v4h4M9 11h6M9 15h4" />
                <circle cx="17.5" cy="17.5" r="3" />
              </svg>
              <strong>Audit</strong><small>Events &amp; history</small>
            </div>
          </div>
        </div>
      </section>

      <section class="pillars">
        @for (pillar of pillars; track pillar.title) {
          <article class="pillar">
            <div class="pillar-icon">
              @switch (pillar.icon) {
                @case ('server') {
                  <svg viewBox="0 0 24 24" aria-hidden="true">
                    <path d="M4 3h16v7H4zM4 14h16v7H4zM8 6.5h.01M8 17.5h.01M12 6.5h5M12 17.5h5" />
                  </svg>
                }
                @case ('file') {
                  <svg viewBox="0 0 24 24" aria-hidden="true">
                    <path d="M6 2.5h8l4 4V22H6zM14 2.5v5h4M9 12h6M9 16h6" />
                  </svg>
                }
                @case ('sdk') {
                  <svg viewBox="0 0 24 24" aria-hidden="true">
                    <path d="m8 6-5 6 5 6M16 6l5 6-5 6M14 3l-4 18" />
                  </svg>
                }
                @case ('shield') {
                  <svg viewBox="0 0 24 24" aria-hidden="true">
                    <path d="M12 2.5 20 6v5.7c0 4.7-3.2 8.4-8 10.3-4.8-1.9-8-5.6-8-10.3V6z" />
                  </svg>
                }
              }
            </div>
            <div class="pillar-copy">
              <h2>{{ pillar.title }}</h2>
              <p>{{ pillar.body }}</p>
            </div>
          </article>
        }
      </section>

      <section class="edp">
        <span class="eyebrow">Enterprise Data Platform</span>
        <h2 class="section-title">
          Acquire, govern, serve, and operate data in one private platform
        </h2>
        <p class="section-sub">
          LakeHold now has its first governed source-to-table path. It is a focused EDP direction,
          not a claim that the full enterprise platform is finished.
        </p>
        <div class="edp-grid">
          @for (capability of edpCapabilities; track capability.title) {
            <article class="edp-capability">
              <span class="status" [class.partial]="capability.status !== 'Available'">{{
                capability.status
              }}</span>
              <h3>{{ capability.title }}</h3>
              <p>{{ capability.body }}</p>
            </article>
          }
        </div>
        <div class="cta">
          <a class="btn btn-primary lg" routerLink="/enterprise-data-platform"
            >Explore the EDP capability map</a
          >
          <a class="btn lg" routerLink="/docs/connectors">Managed connector documentation</a>
          <a class="btn lg" routerLink="/docs/enterprise-data-platform-roadmap"
            >Completed and outstanding work</a
          >
        </div>
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
        <h2 class="section-title">Delivered capabilities and verified release candidates</h2>
        <p class="section-sub">
          August 2026 — optional isolated C# LINQ and a governed source-to-table path, with SQL
          remaining built in and every product boundary explicit and testable.
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

      <section class="roadmap">
        <h2 class="section-title">What's next</h2>
        <p class="section-sub">Stated as plainly as the shipped list, caveats and all.</p>

        <ol class="changelog">
          @for (entry of roadmap; track entry.title) {
            <li class="entry planned">
              <span class="tag">{{ entry.tag }}</span>
              <div class="entry-body">
                <h3>{{ entry.title }}</h3>
                <p>{{ entry.body }}</p>
                @if (entry.caveat) {
                  <p class="caveat"><strong>Honestly:</strong> {{ entry.caveat }}</p>
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
            <h3>Choose LakeHold when</h3>
            <ul>
              <li>Data residency or a security review rules out a hosted warehouse.</li>
              <li>You want your tables as open Parquet in a bucket you control.</li>
              <li>Procurement wants a provable exit, not a clause promising one.</li>
              <li>
                You want one governed API across Java, .NET, Go, and Python, with EF Core where it
                adds value.
              </li>
              <li>You want change data capture without running Debezium and Kafka.</li>
              <li>
                You want SQL clients on the Postgres wire protocol, not a connector to install.
              </li>
              <li>You would rather pay for a VM than per-second compute.</li>
              <li>You need explicit control over compaction, retention, and snapshots.</li>
            </ul>
          </div>
          <div class="col lose">
            <h3>Choose MotherDuck when</h3>
            <ul>
              <li>
                You need per-user accounts and row- or column-level permissions, administered from a
                console.
              </li>
              <li>You want zero operations and no infrastructure to own.</li>
              <li>You need elastic scale-out beyond a single node.</li>
              <li>Hybrid local-and-cloud dual execution matters to you.</li>
              <li>You want a broad library of incremental database and SaaS connectors today.</li>
              <li>Your team is Python-first and wants the most mature UI today.</li>
            </ul>
          </div>
        </div>

        <p class="compare-more">
          <a routerLink="/compare"
            >Full comparison — MotherDuck, ClickHouse, and the cloud warehouses →</a
          >
        </p>
      </section>

      <footer class="foot">
        <p class="domain"><a href="https://lakehold.dev">lakehold.dev</a></p>
        <p>
          Built on
          <a href="https://www.postgresql.org" target="_blank" rel="noopener">PostgreSQL</a>,
          <a href="https://duckdb.org" target="_blank" rel="noopener">DuckDB</a>,
          <a href="https://ducklake.select" target="_blank" rel="noopener">DuckLake</a>, .NET 10,
          and Angular. Apache-2.0.
        </p>
      </footer>
    </div>
  `,
  styleUrls: ['./site-header.css', './landing.component.css'],
})
export class LandingComponent {
  protected readonly edpCapabilities = [
    {
      status: 'Available',
      title: 'Governed lakehouse',
      body: 'PostgreSQL control and metadata, DuckDB compute, open Parquet, tenant identity, audit, time travel, maintenance, backup, and verified eject.',
    },
    {
      status: 'Current connector platform',
      title: 'Managed ingestion foundation',
      body: 'Bounded REST and gRPC full snapshots with schedules, quality contracts, target ownership, fenced publication, telemetry, and retained run evidence.',
    },
    {
      status: 'Partial',
      title: 'Enterprise governance and consumption',
      body: 'HTTP, SQL, EF Core, MCP, owner metadata, and audit exist. Search, classification, lineage graphs, semantic metrics, Power BI, and open multi-engine access remain planned.',
    },
  ];

  protected readonly pillars = [
    {
      icon: 'server',
      title: 'Self-hosted',
      body: 'You own the runtime, data, and keys. No vendor control plane sits in the request path.',
    },
    {
      icon: 'file',
      title: 'Open Parquet',
      body: 'Open format for tables and metadata, with a verified exit path any Parquet reader can use.',
    },
    {
      icon: 'sdk',
      title: 'Java · .NET · Go · Python',
      body: 'First-party source SDKs share one versioned API, with first-class EF Core integration for .NET applications.',
    },
    {
      icon: 'shield',
      title: 'Operator controlled',
      body: 'Compaction, snapshots, backups, and catalog operations stay on schedules you control.',
    },
  ];

  /**
   * Each entry carries its own caveat where one exists. A changelog that only lists what now works
   * is an announcement; the limits are the part an operator actually needs before they rely on it.
   */
  protected readonly changelog = [
    {
      tag: 'Workbench',
      title: 'C# LINQ, isolated and optional',
      body: 'Choose SQL or C# LINQ in the same CodeMirror editor, with catalog-aware completion, line diagnostics, generated parameterized SQL, separate language buffers, history, and language-preserving saved queries. The compiler receives source and schema only; the credential-owning API validates and executes its plan through the same catalog, authorization, limit, telemetry, and audit path as SQL.',
      caveat:
        'The Compose linq profile is opt-in. It accepts one side-effect-free expression and supports queryables plus Count, LongCount, Any, Min, Max, Sum, and Average — not arbitrary LINQPad scripts. Native types without an EF property mapping remain available through SQL.',
    },
    {
      tag: 'Ingestion',
      title: 'Managed full-snapshot and incremental connectors',
      body: 'REST/gRPC snapshots plus PostgreSQL, HubSpot, and Kafka Avro adapters share durable schedules, checkpoints, retry/dead-letter lifecycle, mappings, schema policy, external secret references, quality gates, bounded scratch space, safe egress, target ownership, and atomic DuckLake publication.',
      caveat:
        'Five built-in adapters are not a broad production-certified connector ecosystem. Kafka Avro uses a Confluent-compatible Registry and deployment-owned egress gateways; it is at-least-once, not generic CDC or exactly-once.',
    },
    {
      tag: 'Security',
      title: 'Authentication and tenant identity',
      body: 'Authentication cannot be switched off. API tokens are scoped to a tenant and optionally a single catalog, carrying an owner, editor, or reader role, and people sign in through your identity provider. The sole credential-less HTTP path is an explicitly configured reader for one demo catalog; MCP never accepts it. LakeHold federates authentication and owns authorization: what a signed-in person reaches comes from a membership record you administer in the product, so a provider re-asserting a stale role cannot undo a decision made here. The credential or demo identity names the tenant and the URL segment is validated against it rather than trusted. A reader’s selected catalog is attached read-only, and revoking a token closes the HTTP API and PostgreSQL wire endpoint together because both resolve against the same store.',
      caveat:
        'Authorization stops at the catalog: a role, and optionally one catalog. There are no row or column policies, so anyone who can read a table reads all of it. A read-only selected-catalog attachment does not contain arbitrary SQL from process-visible files, URLs, secrets, or new attachments. Leaving Lakehold:Oidc:Audience empty disables audience validation and accepts every token that issuer minted — LakeHold warns at start-up, and you should treat it as an error.',
    },
    {
      tag: 'Portability',
      title: 'Eject: the exit path in one call',
      body: 'Ejects a verified bundle of your data as ordinary Parquet, plus the metadata catalog when you want history. It re-materialises every table through the catalog rather than copying files, so merge-on-read deletes are applied, superseded update rows are gone, inlined commits are included, and none of DuckLake’s internal columns leak. Every file is counted back through a plain Parquet reader and compared to the catalog before the manifest is written, and the manifest carries per-table row counts, SHA-256 digests, and an HMAC signature when you configure a key.',
      caveat:
        'A copy of the data path is not an eject. Deletes are merge-on-read sidecars only DuckLake understands, so copying files resurrects deleted rows and duplicates updated ones — which is exactly why this exists.',
    },
    {
      tag: 'Compatibility',
      title: 'A PostgreSQL wire endpoint, so SQL clients connect directly',
      body: 'LakeHold speaks the PostgreSQL wire protocol, so a client that already speaks Postgres connects to a catalog with no .mez file, driver, or plugin. The user is the tenant and the database is the catalog, and every statement resolves through the same tenant check, session gate, and query history as an HTTP query, so client traffic is visible in the history for the first time. The 10,000-row ceiling does not apply: rows are encoded straight to the socket rather than materialised, so a result streams instead of being silently truncated.',
      caveat:
        'psql, DBeaver, and Npgsql work today. Power BI does not yet: it reads the server’s type catalogue when connecting, and DuckDB leaves pg_type.typreceive empty, so the driver’s own join comes back with nothing. That is fixable in our compatibility shim rather than in DuckDB, and it is measured rather than guessed — see docs/POSTGRES-WIRE.md. Off by default, with TLS and per-tenant credentials.',
    },
    {
      tag: 'Integration',
      title: 'Change data capture, with nothing extra to run',
      body: 'DuckLake already records what each snapshot changed, so LakeHold exposes it directly: a typed pull API for change pages, and outbound webhooks fired per new snapshot and signed with HMAC-SHA256 over a timestamped base. Updates arrive as a paired pre-image and post-image sharing a row id, so you can take net effect or diff them. No Debezium, no Kafka, no second pipeline.',
      caveat:
        'Delivery is at-least-once. The cursor advances one snapshot at a time and only after a 2xx, so a failing consumer replays rather than skips — make your handler idempotent on (snapshot, row, change type).',
    },
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
        'Retention cannot prune a bucket — DuckDB has no delete for object stores. Set a lifecycle rule on the prefix. LakeHold reports "retention deferred" rather than a "0 pruned" that would read as though it had run.',
    },
    {
      tag: 'Operations',
      title: 'Scheduled maintenance, safe on more than one node',
      body: 'Flush, backup, and compact run on cron schedules you control, with recent runs and their timings readable over the API. Where a catalog can genuinely be shared between nodes, a lease stops every node running the same sweep.',
      caveat:
        'Snapshot expiry and orphan cleanup are deliberately not scheduled. They are irreversible, so they stay manual and dry-run by default.',
    },
    {
      tag: 'Architecture',
      title: 'PostgreSQL-first control and catalog metadata',
      body: 'Tenants, tokens, catalog definitions, subscriptions, and audit history now live in a migrated PostgreSQL control plane. New DuckLake catalogs receive isolated PostgreSQL metadata schemas, while Parquet independently targets local files, S3/S3-compatible storage, GCS, or Azure Blob/ADLS. Credentials remain deployment secrets and are injected only into a worker’s temporary DuckDB session.',
      caveat:
        'DuckDB is still the in-process query engine. Nodes scale tenant and request concurrency; one query is not distributed across a cluster, and local Parquet remains a single-node/shared-filesystem choice.',
    },
  ];

  /**
   * Planned work, in dependency order rather than excitement order. Authentication used to lead this
   * list, because every entry in it is an externally reachable surface and shipping one onto an
   * unauthenticated API would widen the exposure rather than the product. It has shipped, so it now
   * sits in the changelog above and the precondition is met.
   */
  protected readonly roadmap = [
    {
      tag: 'EDP',
      title: 'A broad production-certified connector catalogue',
      body: 'The versioned source SDK, resumable reads, retry/dead-letter lifecycle, mappings, schema policy, external secrets, PostgreSQL, and HubSpot Contacts are implemented. The next connector milestone is a separately distributed and production-certified catalogue driven by demand.',
      caveat:
        'The current SDK lives in the API assembly and the built-in catalogue contains five adapters; no partner ecosystem or broad SaaS coverage is claimed.',
    },
    {
      tag: 'Interop',
      title: 'An Iceberg REST endpoint, so other engines read you live',
      body: 'Eject proves the data is portable, but it is a batch artifact. Serving the Iceberg REST Catalog protocol would let Spark, Trino, Snowflake, or DuckDB attach to a LakeHold catalog directly and read it live, with no export step and the same credential-bound tenant/catalog routing as the query path. That would still require its own containment and authorization review.',
      caveat:
        'DuckLake’s Iceberg support is a copy between formats, not this — the translation is ours to write, and whether merge-on-read delete sidecars map cleanly onto Iceberg deletes is unverified. That test comes before the promise.',
    },
    {
      tag: '.NET',
      title: 'A client package with a typed change stream',
      body: 'The EF Core model already describes both your application and your lake. A LakeHold client would make that installable: migrations that define lake tables, results deserialised into your own entity types, and the change feed surfaced as ChangeEvent<T> with pre-image and post-image already paired into Before and After.',
      caveat:
        'Today there is no client package — the .NET story is a property of the architecture and the provider, not something you can add to a csproj yet.',
    },
    {
      tag: 'Assurance',
      title: 'Continuous exit attestation',
      body: 'Eject proves the exit path when someone calls it. On a schedule, it would prove it continuously: as of this snapshot, every table re-materialised, row counts verified against the catalog, read back with no DuckLake extension loaded — kept as a signed, dated artifact instead of an on-demand call.',
      caveat:
        'The value is in it failing loudly. An attestation that has gone stale relative to the newest snapshot has to read as a warning, or silence stops meaning verified.',
    },
  ];

  protected readonly stats = [
    { value: '250k', label: 'Rows read back from bare Parquet' },
    { value: '30', label: 'Metadata tables backed up and restored' },
    { value: '706', label: 'Backend and frontend checks in the full gate' },
    { value: '0', label: 'Vendor services in the query path' },
    { value: 'Apache-2.0', label: 'Permissive open-source licence' },
  ];
}
