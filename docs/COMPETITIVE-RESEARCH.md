# Competitive research — July 2026

A dated snapshot of what competitors shipped, what the DuckLake ecosystem is building, and what
users are actually asking for. [`ARCHITECTURE.md`](ARCHITECTURE.md) states Lakehold's *position*;
this document is the evidence behind it and the record of when that evidence was gathered.

**Read it as perishable.** Every claim below is anchored to a date and a source. A row in the
feature matrix that contradicts this document means one of the two is stale — check the date before
assuming which.

## Method and its limits

Stated first, because a competitive document that hides its sourcing is marketing.

- **Vendor release notes and docs** for what shipped, with dates. Strongest evidence here.
- **`duckdb/ducklake` issues and discussions**, ranked by reaction count, for demand. This is the
  best available signal for what DuckLake users want: it is public, quantified, and unfiltered by a
  vendor's roadmap.
- **Industry surveys** for direction, not for specifics.
- **Not consulted:** Reddit (inaccessible to the crawler used). The Hacker News thread on DuckLake
  v1.0 drew four comments and carried no usable signal. So "what users want" below rests on the
  issue trackers, and community *sentiment* is under-sampled relative to community *requests*.

Counts are as of 25 July 2026 and only move upward; treat them as a floor.

## 1. The upstream shift that matters most: Quack

DuckDB Labs released **Quack**, a client/server protocol over HTTP that lets multiple DuckDB
instances work against the same database over a network. MIT-licensed, shipping as an autoloadable
core extension in DuckDB 1.5.3, with a production-ready release targeted at **DuckDB 2.0 in late
2026**. Reported at roughly 3.5× Arrow Flight's throughput.

The load-bearing detail is not the protocol. It is that Quack is planned to be **integrated into
DuckLake so that DuckDB itself can act as a remotely-accessible catalog server**. An open discussion
on the DuckLake tracker asks whether "Quack server as shared DuckLake catalog in a multi-tenant SaaS
environment" is the right pattern
([discussion #1315](https://github.com/duckdb/ducklake/discussions/1315)) — that is Lakehold's
category, being asked upstream, and as of this writing unanswered.

**What this changes.** *Remote access to a DuckLake catalog* stops being a differentiator once it is
a core extension. It was never one Lakehold claimed loudly — the wire endpoint is documented as
parity, not differentiation ([`POSTGRES-WIRE.md`](POSTGRES-WIRE.md)) — but the surrounding argument
has to be stated deliberately rather than assumed: what Quack does not carry is tenancy, credentials,
audit, maintenance policy, verified eject, or the change feed. Those are the control plane, and the
control plane is the product.

**What it does not change.** Quack is not the Iceberg REST Catalog and does not serve external
engines. Spark, Trino, and Snowflake do not learn to read a DuckLake table because DuckDB grew a
remote protocol. USP 5 is untouched by this.

**Open decision, recorded here rather than resolved.** Whether Lakehold serves Quack alongside the
PostgreSQL wire endpoint is a real question with a real deadline — DuckDB 2.0. The PG endpoint
unlocks BI tools; Quack would unlock DuckDB clients at native speed. Both are parity plays. Neither
is USP 5. This document's recommendation is to *decide before 2.0 lands*, not to build now.

## 2. DuckLake itself

**v1.0 shipped April 2026** — production-ready specification, extension, and a backward-compatibility
guarantee. **v1.1 is expected September 2026.** Format-stability risk, which was a live concern when
Lakehold picked DuckLake, is materially reduced.

The [published roadmap](https://ducklake.select/roadmap) splits into two tiers, and the split matters
more than the contents:

| Planned (next release) | Future work / *looking for funding* |
|---|---|
| Inlining for the `VARIANT` type | User-defined types |
| Multi-deletion-vector Puffin files | **Role-based access control** |
| | **Materialized views and incremental maintenance** |
| | **Protected snapshots** |
| | **Branching and merge** |
| | Parquet Bloom filter / metadata-scan read performance |
| | `PRIMARY KEY` syntax without enforcement, fixed-size arrays |
| | PostgreSQL round-trip reduction, MySQL robustness |

Three of those bolded items appear on Lakehold's own roadmap as Lakehold features: catalog branching,
RBAC beyond tenancy, and — implicitly, via the semantic layer — materialized views. They sit in the
*unfunded* tier upstream, so nothing is imminent, but the ownership question is worth settling before
building: a feature the format may absorb is a poor place to spend a differentiation budget, while a
feature the format will never own (tenancy, attestation, .NET model sharing) is a good one.

## 3. MotherDuck — the closest peer

2026 was an agentic year followed by an enterprise catch-up quarter.

| Shipped | Date |
|---|---|
| **Flights** — agent-native ingest and transform pipelines on a Python runtime, scheduled, built by an agent through the MCP server | 10 Jun (preview) → all plans by 9 Jul |
| **Dives** — agent-built, shareable live dashboards over composable SQL | GA 10 Jun |
| **Role-based access control** (Business and Enterprise) | 23 Jul |
| Read/write Cloudflare R2 Data Catalog (Iceberg); writes to Databricks-managed Iceberg tables | 9–23 Jul |
| Server-side Iceberg attach for external catalogs (preview) | 2 Jul |
| **dbt Cloud support through the Postgres endpoint** | 2 Jul |
| SOC 2 Type II, GDPR, tiered support, read scaling, EU and APAC regions | H1 |

Two observations are worth more than the list.

**The dbt Cloud integration arrived through their PostgreSQL endpoint.** That is independent
confirmation that a PG wire surface is an *integration* surface and not merely a BI convenience —
the same endpoint Lakehold already ships. The parity framing in `POSTGRES-WIRE.md` is right, and the
value of that parity is higher than "BI tools can connect" implies.

**Time travel is not the gap it was assumed to be.** MotherDuck ships `CREATE SNAPSHOT`,
`ALTER DATABASE SET SNAPSHOT`, `UNDROP DATABASE`, point-in-time restore with up to 90 days of
retention, and zero-copy database clones via `CREATE DATABASE` / `COPY FROM DATABASE (OVERWRITE)`,
all built on Differential Storage. The feature matrix's ✅ on that row was checked against the
documentation and stands. Their zero-copy clone is close enough to git-style branching to move that
row off ❌ — it is a clone, not a branch with merge, so ⚠️ is the honest mark.

**Where they genuinely do not go:** row- and column-level security. This is documented as a
deliberate design choice — access is granted at the database level, whole database or nothing, with
multi-tenancy handled by separating databases rather than by fine-grained policy. The matrix
previously scored them ✅ on that row; it was wrong and is corrected.

## 4. Dremio, Databricks, Snowflake

**Dremio** repositioned as "the agentic lakehouse". Concretely: an open-source **MCP server** that
works against on-premise Dremio Software as well as Cloud (community-supported, not covered by
Dremio's support policy); a **built-in Nessie-powered Iceberg catalog** with RBAC, automated
compaction and garbage collection, and branching, deployable as a full stack by Helm chart; and a
Community Edition that now bundles Apache Polaris. Row-access and column-masking policies are
documented product features. Dremio remains the sharpest direct competitor and got sharper on the
catalog axis specifically.

**Snowflake and Databricks** converged on the same four bets at their 2026 summits: agents as the
primary compute unit, MCP as the protocol layer, a semantic or "context" layer serving certified
metric meaning to those agents (Snowflake Semantic Views, Databricks Metric Views), and governance
extended to cover AI systems. The convergence is the signal — when two competitors who agree on
nothing else ship the same architecture in the same quarter, it is the category moving, not a
product bet.

The sober caveat from practitioners: MCP interoperability is looser than the marketing implies. Tool
naming and authentication models differ per server, so "we have an MCP server" is a weaker claim
than it sounds, and *how* a server authenticates is where the differentiation actually sits.

## 5. What users ask for

From the `duckdb/ducklake` trackers, by reaction count. This is demand, not roadmap.

| Ask | 👍 |
|---|---|
| Vortex file format support ([#566](https://github.com/duckdb/ducklake/discussions/566)) | 35 |
| **Partitioning at `CREATE TABLE`** ([#301](https://github.com/duckdb/ducklake/issues/301)) | 26 |
| Export an Iceberg metadata snapshot ([#37](https://github.com/duckdb/ducklake/discussions/37)) | 17 |
| Git-style branching RFC ([#720](https://github.com/duckdb/ducklake/discussions/720)) | 12 |
| Lance format (#432) · per-table snapshot expiry (#144) · `target_file_size` with partitioning (#224) | 11 each |
| Multi-catalog support (#1276) · table-level conflict checks too coarse (#1253) | 8 each |
| `ARRAY` (#123) · `VARIANT` (#598) · JDBC connection string (#114) · SQL Server as catalog (#892) · return snapshot id from DML (#136) | 4–7 |

Partitioning is the top *issue* by a factor of 2.4 over the next one, and #224 is the same complaint
from the other side — partitioning that exists but cannot be sized. Read together, physical layout
control is the loudest unmet need in DuckLake.

**#37 deserves separate attention.** "Export an Iceberg metadata snapshot" at 17 upvotes is demand
for precisely the translation half of USP 5. Someone wants a DuckLake table to present itself to the
Iceberg world, and the request is sitting upstream unbuilt.

### The operational cluster

Individually small, collectively the most actionable finding in this document. Every one of these is
a self-host operator getting hurt by maintenance:

- Orphaned files left behind by failed inserts
  ([#300](https://github.com/duckdb/ducklake/issues/300))
- `delete_orphaned_files` performing an unbounded full-bucket `LIST` and timing out at scale
  ([#1090](https://github.com/duckdb/ducklake/issues/1090))
- `CHECKPOINT` deleting catalog-active files on `abfss://` through a URL-form mismatch
  ([#1105](https://github.com/duckdb/ducklake/issues/1105))
- Connection-pool timeouts against a PostgreSQL catalog
  ([#1031](https://github.com/duckdb/ducklake/issues/1031))
- Concurrent-commit primary-key collisions generating 1 MB/hour of logs
  ([#1094](https://github.com/duckdb/ducklake/issues/1094))
- Deleted rows reappearing where Parquet and inlined tombstones overlap on one data file
  ([#1084](https://github.com/duckdb/ducklake/issues/1084))
- `merge_adjacent_files` leaking memory when empty tables are present (#929)

Lakehold already sits directly on this surface: explicit maintenance operations, dry-run by default
(invariant 10), per-catalog leasing (invariant 14), and scheduling. That is the right shape; the
finding is that the *hard* parts — bounded cleanup, per-table expiry, correctness under concurrent
commit — are unsolved upstream and painful in practice.

Note the overlap with Lakehold's own guarantees. #1084 (deleted rows reappearing where inlined
tombstones and Parquet overlap) is the same class of failure that invariants 12 and 15 exist to
prevent, and #300 (orphans from failed inserts) is exactly what invariant 11 keeps backups clear of.
The exit path is not just a differentiator here; it is protection against a live upstream bug class.

### Broader survey signal

From the 2026 State of Data Engineering and adjacent reporting, for direction only:

- Data governance is a top-three 2026 priority for **40.9%** of data leaders, ranking above
  AI-specific initiatives.
- **59%** name "pressure to move fast" as their top pain point; **51%** name lack of ownership.
  Bottlenecks are reported as organisational rather than technical.
- Strong demand for capability in **data modeling and semantic layers** — the same layer the vendors
  are shipping for agents to consume.
- Silent data-quality issues persisting for days without observability tooling; ingestion
  reliability as the recurring complaint; "streaming-first" as the stated architectural direction.

## 6. What follows for Lakehold

Recommendations, not commitments. Each names the evidence above that produced it.

**Move the MCP server ahead of the rest of "Next".** Every competitor shipped one this year, it is
cheap on an API that already has tokens, roles, and audit, and Lakehold has a claim none of them can
make: **capability is attachment** (invariants 4 and 20), so a read-only agent token yields a
read-only *attachment* and a write fails in the engine. Everyone else enforces agent safety in a
policy layer above the SQL. Given the practitioner complaint that MCP authentication models are where
servers actually differ, that is the right differentiator to lead with.

**Keep the Iceberg REST Catalog endpoint as the moat play; the research strengthens it.** IRC is now
the universal catalog protocol, catalog federation is the stated industry direction, MotherDuck added
server-side Iceberg attach and Databricks-managed Iceberg writes, and #37 shows the demand exists
inside the DuckLake community itself. Nothing serves *live* DuckLake as IRC. Quack does not cover
this, and DuckLake 0.3's Iceberg support is a copy, not a serving path — the trap `ARCHITECTURE.md`
already names.

**Decide the Quack position before DuckDB 2.0.** Not necessarily build. The decision has a date on
it, and it is the one item in this research that can move Lakehold's positioning out from under it.

**Treat maintenance-at-scale as a workstream, not a background chore.** Section 5's operational
cluster is the clearest underserved need found anywhere in this research, it lands on a surface
Lakehold already owns, and being demonstrably safer than raw DuckLake is a claim supportable with
tests rather than with positioning.

**Promote the semantic layer out of "Later".** It has stopped being a BI nicety and become the
substrate agents consume — Semantic Views, Metric Views, context layers over MCP. Generating it from
the EF Core model is something no JVM lakehouse can do, and it compounds with the MCP server rather
than competing with it for the same slot.

**Live parity gaps**, all confirmed above: RBAC beyond tenancy (MotherDuck shipped 23 Jul), managed
ingestion (Flights now on every MotherDuck plan), and branching (Dremio via Nessie today, DuckLake
eventually). Row- and column-level security is *not* the gap it appeared to be — the closest peer has
deliberately declined to build it.
