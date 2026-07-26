import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BrandMarkComponent } from './brand-mark.component';

/**
 * Strength or limitation on a given axis, judged from the reader's point of view — not ours.
 *
 * The rule is applied symmetrically: if elastic scale earns a competitor `good`, our single node
 * earns `weak` on the same row, and vice versa. An earlier version marked our weaknesses honestly
 * but left competitors' matching strengths `neutral`, which produced a scoreboard where MotherDuck
 * and the cloud warehouses could not score green anywhere. That is the kind of quiet thumb on the
 * scale this page exists to avoid.
 *
 * Axes that are genuinely a matter of preference rather than capability — maintenance philosophy,
 * cost shape, both of which depend entirely on what you want and how much data you have — are
 * `neutral` for everyone, and the text is left to speak.
 */
type Tone = 'good' | 'weak' | 'neutral';

interface Cell {
  text: string;
  tone: Tone;
}

interface Row {
  dimension: string;
  lakehold: Cell;
  motherduck: Cell;
  clickhouse: Cell;
  cloud: Cell;
}

interface HeadToHead {
  name: string;
  summary: string;
  chooseUs: string[];
  chooseThem: string[];
}

/** Competitive comparison page. */
@Component({
  selector: 'lh-comparison',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandMarkComponent, RouterLink],
  templateUrl: './comparison.component.html',
  styleUrl: './comparison.component.css',
})
export class ComparisonComponent {
  protected readonly rows: Row[] = [
    {
      dimension: 'Deployment',
      lakehold: { text: 'Self-hosted anywhere, incl. air-gapped', tone: 'good' },
      motherduck: { text: 'Hosted catalog; local or cloud compute', tone: 'neutral' },
      clickhouse: { text: 'Self-hosted or ClickHouse Cloud', tone: 'good' },
      cloud: { text: 'Hosted service only', tone: 'weak' },
    },
    {
      dimension: 'Where your data lives',
      lakehold: { text: 'Your disk or object store, under your control', tone: 'good' },
      motherduck: { text: 'Managed storage or your own bucket', tone: 'good' },
      clickhouse: { text: 'Your disks, or their cloud', tone: 'good' },
      cloud: { text: 'Their account, or external tables', tone: 'weak' },
    },
    {
      dimension: 'Accounts, SSO, permissions',
      lakehold: { text: 'API tokens, OIDC, three roles; opt-in, no admin UI or row policies', tone: 'weak' },
      motherduck: { text: 'Accounts, SSO, org roles', tone: 'good' },
      clickhouse: { text: 'Users, roles, row policies', tone: 'good' },
      cloud: { text: 'Mature RBAC, SSO, lineage', tone: 'good' },
    },
    {
      dimension: 'Table format',
      lakehold: { text: 'DuckLake \u2014 plain Parquet + SQL catalog', tone: 'good' },
      motherduck: { text: 'DuckLake \u2014 same open format', tone: 'good' },
      clickhouse: { text: 'MergeTree, proprietary on disk', tone: 'weak' },
      cloud: { text: 'Delta / Iceberg, now genuinely open', tone: 'good' },
    },
    {
      dimension: 'Read data without the product',
      lakehold: { text: 'Yes \u2014 tested, see exit path', tone: 'good' },
      motherduck: { text: 'Yes, DuckLake is open', tone: 'good' },
      clickhouse: { text: 'Export required', tone: 'weak' },
      cloud: { text: 'Yes, via Iceberg / Delta readers', tone: 'good' },
    },
    {
      dimension: 'Other engines read it live',
      lakehold: { text: 'Eject or export today; Iceberg REST planned', tone: 'weak' },
      motherduck: { text: 'Direct with BYO compute; catalog remains hosted', tone: 'good' },
      clickhouse: { text: 'Its own protocols; export for others', tone: 'weak' },
      cloud: { text: 'Yes — via their catalog endpoints', tone: 'good' },
    },
    {
      dimension: 'Time travel',
      lakehold: { text: 'Yes — query your data from an earlier point in time', tone: 'good' },
      motherduck: { text: 'Yes', tone: 'good' },
      clickhouse: { text: 'No first-class equivalent', tone: 'weak' },
      cloud: { text: 'Yes, mature', tone: 'good' },
    },
    {
      dimension: 'Verified, signed export',
      lakehold: { text: 'One call — row-count attested and signed', tone: 'good' },
      motherduck: { text: 'Manual export; nothing attests it', tone: 'weak' },
      clickhouse: { text: 'Manual export', tone: 'weak' },
      cloud: { text: 'Manual unload; nothing attests it', tone: 'weak' },
    },
    {
      dimension: 'Change data capture',
      lakehold: { text: 'Built in — typed feed + signed webhooks', tone: 'good' },
      motherduck: { text: 'Limited; not exposed directly', tone: 'weak' },
      clickhouse: { text: 'Kafka engine or external tooling', tone: 'neutral' },
      cloud: { text: 'Yes — CDF / streams, mature', tone: 'good' },
    },
    {
      dimension: 'AI / MCP',
      lakehold: { text: 'Authenticated MCP; read tools + operator-gated writes', tone: 'good' },
      motherduck: { text: 'Managed MCP with sandboxed compute', tone: 'good' },
      clickhouse: { text: 'Open-source and managed remote MCP', tone: 'good' },
      cloud: { text: 'Managed AI and agent platforms with MCP', tone: 'good' },
    },
    {
      dimension: 'BI tools (Power BI, Tableau)',
      lakehold: { text: 'Postgres wire protocol; Power BI blocked on type loading', tone: 'weak' },
      motherduck: { text: 'Postgres endpoint; connector for older tools', tone: 'good' },
      clickhouse: { text: 'Native connectors and JDBC/ODBC', tone: 'good' },
      cloud: { text: 'First-class connectors everywhere', tone: 'good' },
    },
    {
      dimension: 'Maintenance control',
      lakehold: { text: 'Explicit, dry-run by default', tone: 'neutral' },
      motherduck: { text: 'Automatic, not exposed', tone: 'neutral' },
      clickhouse: { text: 'Explicit merges and TTLs', tone: 'neutral' },
      cloud: { text: 'Automatic, partly exposed', tone: 'neutral' },
    },
    {
      dimension: '.NET / EF Core',
      lakehold: { text: 'One model for app and lake; client package pending', tone: 'good' },
      motherduck: { text: 'Community drivers; Python/JS first', tone: 'weak' },
      clickhouse: { text: 'Solid ADO.NET client, no ORM story', tone: 'neutral' },
      cloud: { text: 'JDBC/ODBC; .NET is second-class', tone: 'weak' },
    },
    {
      dimension: 'Scale ceiling',
      lakehold: { text: 'One node \u2014 GB to low TB', tone: 'weak' },
      motherduck: { text: 'Elastic, scales past a node', tone: 'good' },
      clickhouse: { text: 'Clustered, petabyte-scale', tone: 'good' },
      cloud: { text: 'Effectively unlimited', tone: 'good' },
    },
    {
      dimension: 'Concurrent writers',
      lakehold: { text: 'Single writer per catalog', tone: 'weak' },
      motherduck: { text: 'Managed', tone: 'good' },
      clickhouse: { text: 'High concurrency', tone: 'good' },
      cloud: { text: 'High concurrency', tone: 'good' },
    },
    {
      dimension: 'Operational burden',
      lakehold: { text: 'You run it', tone: 'weak' },
      motherduck: { text: 'None', tone: 'good' },
      clickhouse: { text: 'High if self-hosted', tone: 'weak' },
      cloud: { text: 'Low', tone: 'good' },
    },
    {
      dimension: 'Licence',
      lakehold: { text: 'Apache-2.0', tone: 'good' },
      motherduck: { text: 'Proprietary', tone: 'weak' },
      clickhouse: { text: 'Apache-2.0; Cloud proprietary', tone: 'good' },
      cloud: { text: 'Proprietary', tone: 'weak' },
    },
    {
      dimension: 'Cost shape',
      lakehold: { text: 'A VM and a bucket', tone: 'neutral' },
      motherduck: { text: 'Free Lite; Business $250/org/mo + usage', tone: 'neutral' },
      clickhouse: { text: 'Free self-hosted; Cloud pay-as-you-use', tone: 'neutral' },
      cloud: { text: 'Usage / credit based; enterprise spend varies', tone: 'neutral' },
    },
  ];

  protected readonly headToHead: HeadToHead[] = [
    {
      name: 'MotherDuck',
      summary:
        'The closest comparison: the same engine and the same table format, with a different control model. MotherDuck hosts the catalog and can manage storage and compute; Lakehold puts the whole service under your control. Query semantics are otherwise close — a query that runs on one generally runs on the other.',
      chooseUs: [
        'Data residency, a security review, or an air-gapped network rules out a hosted service.',
        'You want the metadata catalog under your control rather than hosted by another service.',
        'Your stack is .NET and you want EF Core and analytics sharing one model.',
        'You want the compaction and retention knobs exposed rather than managed for you.',
        'A predictable VM bill beats per-second billing for your workload.',
      ],
      chooseThem: [
        'You need per-user accounts and row- or column-level permissions with a console to administer them — Lakehold authenticates with tenant-scoped API tokens, OIDC, and three roles, but per-user administration is not a product surface yet.',
        'You need a shared multi-tenant production service today — Lakehold still has same-name catalog isolation work to complete.',
        'You want zero operations and nothing to run.',
        'You need to scale past a single node without re-architecting.',
        'Hybrid local-and-cloud dual execution is valuable to you — it is genuinely clever and we have not replicated it.',
        'You want managed ingestion and a more mature web UI today.',
        'Your team is Python-first.',
      ],
    },
    {
      name: 'ClickHouse',
      summary:
        'The strongest alternative if self-hosting is the requirement, and on raw scale and concurrency it beats us outright. The real difference is storage philosophy: ClickHouse owns its on-disk format, Lakehold leaves plain Parquet in your bucket.',
      chooseUs: [
        'You want an open Parquet and DuckLake storage contract with a verified eject path.',
        'You need transactions, snapshots, and time travel over your tables.',
        'Your data fits comfortably on one node and you would rather not run a cluster.',
        'You are a .NET shop and want an ORM story, not just a driver.',
      ],
      chooseThem: [
        'You are past a few terabytes, or heading there quickly.',
        'You need high write concurrency or many simultaneous readers.',
        'You want sub-second dashboards over very large tables.',
        'You have the DevOps capacity to run a cluster properly — and it is a real cluster.',
        'You need a mature ecosystem and a long operational track record.',
      ],
    },
    {
      name: 'Snowflake / Databricks',
      summary:
        'Grouped because by 2026 they have converged: both do lakehouse workloads, both support open table formats, and both are excellent. They are also a different category of spend, and the honest comparison is scope, not features.',
      chooseUs: [
        'Your workload is far smaller than their pricing assumes — a single node genuinely covers it.',
        'You want the whole platform to be inspectable and Apache-2.0.',
        'You need to run somewhere they do not, including on a laptop or offline.',
        'Cost predictability matters more than elasticity.',
      ],
      chooseThem: [
        'You are operating at real scale, with many teams and governed sharing across them.',
        'You need mature governance, lineage, and enterprise compliance out of the box.',
        'ML, Spark, and agentic AI workloads sit alongside your SQL.',
        'You want an ecosystem of connectors and consultants that already exists.',
        'Nobody on your team should be thinking about compaction schedules.',
      ],
    },
  ];
}
