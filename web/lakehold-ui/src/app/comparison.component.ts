import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';

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
  imports: [RouterLink],
  templateUrl: './comparison.component.html',
  styleUrl: './comparison.component.css',
})
export class ComparisonComponent {
  protected readonly rows: Row[] = [
    {
      dimension: 'Deployment',
      lakehold: { text: 'Self-hosted anywhere, incl. air-gapped', tone: 'good' },
      motherduck: { text: 'Hosted service only', tone: 'weak' },
      clickhouse: { text: 'Self-hosted or ClickHouse Cloud', tone: 'good' },
      cloud: { text: 'Hosted service only', tone: 'weak' },
    },
    {
      dimension: 'Where your data lives',
      lakehold: { text: 'Your bucket, always', tone: 'good' },
      motherduck: { text: 'Their account; BYO bucket on paid tiers', tone: 'weak' },
      clickhouse: { text: 'Your disks, or their cloud', tone: 'good' },
      cloud: { text: 'Their account, or external tables', tone: 'weak' },
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
      dimension: 'Time travel',
      lakehold: { text: 'Snapshots + AS OF queries', tone: 'good' },
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
      dimension: 'Maintenance control',
      lakehold: { text: 'Explicit, dry-run by default', tone: 'neutral' },
      motherduck: { text: 'Automatic, not exposed', tone: 'neutral' },
      clickhouse: { text: 'Explicit merges and TTLs', tone: 'neutral' },
      cloud: { text: 'Automatic, partly exposed', tone: 'neutral' },
    },
    {
      dimension: '.NET / EF Core',
      lakehold: { text: 'First-class \u2014 one model for app and lake', tone: 'good' },
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
      lakehold: { text: 'Apache-2.0, no open-core catch', tone: 'good' },
      motherduck: { text: 'Proprietary', tone: 'weak' },
      clickhouse: { text: 'Apache-2.0', tone: 'good' },
      cloud: { text: 'Proprietary', tone: 'weak' },
    },
    {
      dimension: 'Cost shape',
      lakehold: { text: 'A VM and a bucket', tone: 'neutral' },
      motherduck: { text: 'Free tier; Business from ~$250/mo', tone: 'neutral' },
      clickhouse: { text: 'Free self-hosted; Cloud from ~$66/mo', tone: 'neutral' },
      cloud: { text: 'Credit-based; ~$28\u201336k/yr mid-size team', tone: 'neutral' },
    },
  ];

  protected readonly headToHead: HeadToHead[] = [
    {
      name: 'MotherDuck',
      summary:
        'The closest comparison: the same engine and the same table format, with the opposite deployment model. This is a choice about where the data sits and who operates it, not about query semantics — a query that runs on one generally runs on the other.',
      chooseUs: [
        'Data residency, a security review, or an air-gapped network rules out a hosted service.',
        'You want bring-your-own-bucket as the default, not a paid tier.',
        'Your stack is .NET and you want EF Core and analytics sharing one model.',
        'You want the compaction and retention knobs exposed rather than managed for you.',
        'A predictable VM bill beats per-second billing for your workload.',
      ],
      chooseThem: [
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
        'You want open Parquet on disk that other engines can read without an export step.',
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
