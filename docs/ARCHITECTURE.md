# Lakehold — architecture and positioning

## What it is

Lakehold is an open-source lakehouse platform: a multi-tenant query service, catalog, and web
IDE built on **DuckDB** (the engine) and **DuckLake** (the open table format), with a .NET backend
and an Angular frontend.

It is the self-hostable answer to MotherDuck.

## Value proposition

> **Your lakehouse, your bucket, your VPC.** A serverless-feeling DuckDB warehouse that runs on
> your own infrastructure, stores every byte as open Parquet you can read without us, and speaks
> .NET natively.

Four claims, each defensible against MotherDuck:

### 1. Self-hostable, no data egress

MotherDuck is a hosted service. Your data goes to their account, and the compute that reads it is
theirs. That is disqualifying for regulated industries, data-residency requirements, and anyone
whose security review asks "where does the data live?"

Lakehold deploys into your environment — laptop, single VM, Kubernetes, or an air-gapped network.
The default deployment stores data in your own object store (BYOB is the *only* mode, not a paid
add-on). There is no vendor-hosted control plane in the loop.

### 2. Open format, no lock-in

DuckLake stores table data as plain Parquet files and metadata as ordinary SQL tables (DuckDB file
or PostgreSQL). Both halves are readable without Lakehold running.

The Parquet is genuinely Parquet, and we checked with a reader that has nothing to do with DuckDB.
Apache Arrow (pyarrow 25) reports `PAR1` framing, format version 1.0, standard `INT64` / `BYTE_ARRAY`
physical types with `Int` / `String` logical annotations, Snappy compression, and — notably — **no
custom key-value metadata in the footer at all**. Iceberg and Delta both write format-specific
metadata into the Parquet footer; DuckLake writes none, because all of it lives in the SQL catalog.

But **"DuckLake" is not a synonym for "Parquet"**, and the distinction matters:

- DuckLake is *Parquet files + a SQL catalog + layout conventions*. The catalog is load-bearing, not
  a cache. Deletions and file liveness live only there.
- Files written by an `UPDATE` carry extra `_ducklake_internal_row_id` and
  `_ducklake_internal_snapshot_id` columns, so **files belonging to one table can have different
  schemas** and a reader must union by name.
- Deletes are merge-on-read: a `*-delete.parquet` sidecar holds `file_path` + row position. It is a
  Parquet container with a DuckLake-specific schema — no Spark, Trino, or Iceberg reader knows to
  interpret it as deletions.

So a naive `SELECT * FROM read_parquet('s3://bucket/**/*.parquet')` is *not* a valid exit path: it
resurrects deleted rows, duplicates updated ones, and a recursive glob across tables with differing
schemas either errors or silently misaligns columns. The correct, tested recovery procedure — and
what is genuinely unrecoverable without the catalog — is in [`docs/EXIT-PATH.md`](EXIT-PATH.md).

The trade-off is deliberate on DuckLake's part: putting metadata in a database rather than in
manifest files makes commits and snapshot queries far cheaper than Iceberg's file-based manifests,
at the cost of a catalog you must back up. The exit path is a feature and we test it — including
the failure modes.

### 3. .NET-native, application-integrated

This is the differentiator no competitor has. Via
[`DuckDB.EFCoreProvider`](https://github.com/skuirrels/DuckDB.EFCoreProvider), your **application's
EF Core model and your lakehouse tables are the same model**. You write `DbSet<Order>`, run
migrations, and the analytics layer already knows the schema — no separate dbt model, no schema
drift between the OLTP definition and the warehouse definition.

MotherDuck's SDK story is Python and JavaScript. .NET shops are second-class there. Here they are
the primary audience.

### 4. Predictable cost

MotherDuck removed its $25 tier in 2026 and raised Business from $100 to $250/month, billing
serverless compute per second. Lakehold's cost is the VM and the bucket. A single 8-core node
serves a meaningful analytics workload because DuckDB is genuinely that efficient.

## Where we do not win

Stated plainly, because a positioning document that only lists strengths is marketing, not
engineering:

| MotherDuck advantage | Reality for Lakehold |
|---|---|
| Zero operations | You run it. We ship Aspire + containers, but you own uptime. |
| Elastic scale-out | Bounded by node size. Read replicas help reads; writes are single-writer. |
| Dual execution (local ↔ cloud hybrid) | Genuinely novel and patent-adjacent. Not replicated. |
| Managed ingestion connectors | We ship file/object ingestion. No hosted Fivetran-alikes. |
| Mature web UI | Theirs is years ahead. Ours is a focused SQL IDE. |

The honest summary: **we trade elasticity and zero-ops for control, openness, and .NET integration.**

## Two-plane architecture

The central design decision, and the one most likely to be questioned. Note that the planes are
split by whether the workload has a *model*, not by which library they use — both sit on the same
provider:

```
┌─────────────────────────────────────────────────────────┐
│  Angular SQL IDE  (editor · catalog explorer · results) │
└───────────────────────────┬─────────────────────────────┘
                            │ REST
┌───────────────────────────▼─────────────────────────────┐
│  Lakehold.Api                                          │
│    auth · tenant resolution · request shaping           │
└──────────┬───────────────────────────────┬──────────────┘
           │                               │
┌──────────▼──────────────┐   ┌────────────▼──────────────┐
│  CONTROL PLANE          │   │  DATA PLANE               │
│  Lakehold.ControlPlane │   │  Lakehold.Engine         │
│                         │   │                           │
│  ControlPlaneContext    │   │  LakeContext              │
│  (EF model, migrations) │   │  (model-less, dynamic SQL)│
│                         │   │                           │
│  tenants, catalogs,     │   │  Duckling sessions,       │
│  saved queries, history,│   │  arbitrary user SQL,      │
│  tokens, audit          │   │  schema introspection,    │
│                         │   │  maintenance jobs         │
└─────────────────────────┘   └───────────────────────────┘
        both on DuckDB.EFCoreProvider 1.13.0
```

### Both planes, one provider

The split is by *model*, not by dependency. Both planes run on
[`DuckDB.EFCoreProvider`](https://github.com/skuirrels/DuckDB.EFCoreProvider) 1.13.0:

- **Control plane** — a known EF model with migrations, relationships, and change tracking. Native
  DuckDB storage, because it needs sequences and `RETURNING`, which DuckLake does not provide.
- **Data plane** — a **model-less** `DbContext` (`LakeContext`) over `UseDuckLake`, serving
  arbitrary SQL through the provider's streaming `SqlQueryDynamicRawAsync`. No `DbSet`, no change
  tracker, no LINQ pipeline: those exist for known schemas, and this one is discovered at runtime.

This was not the original design. Against provider 1.12.0 the data plane used raw `DuckDB.NET`,
because EF Core required a CLR type per result shape and a lakehouse has none to offer. Provider
1.13.0 added streaming dynamic queries and a typed DuckLake maintenance facade, which removed the
reason for the split — so roughly 150 lines of hand-rolled connection lifecycle, type
normalisation, and `CALL` string-building were deleted.

The full before/after is in [`docs/PROVIDER-FEEDBACK.md`](PROVIDER-FEEDBACK.md). The short version:
**EF Core's machinery is still not on the path for arbitrary SQL, but you no longer have to leave
the provider to skip it.**

### Ducklings: the isolation unit

A **Duckling** is one tenant's compute session — an in-memory DuckDB instance with that tenant's
DuckLake catalog attached and selected, plus a memory limit and thread budget.

Isolation is structural: a tenant can only reference the one catalog attached to its own session,
so cross-tenant access is prevented by *what is attached* rather than by inspecting the SQL a
tenant submits. SQL parsing as a security boundary is a losing game; attachment scope is not.

Because all durable state lives in DuckLake (metadata catalog + Parquet), a session holds no data
and is cheap to evict. That is what makes an aggressive idle timeout safe, and it is the same
insight behind MotherDuck's hypertenancy.

## Verified engine behaviour

Established by running DuckDB 1.5.3 + DuckLake, not read from documentation:

1. **Small writes are inlined into the metadata catalog, not written as Parquet.** A two-row insert
   produced zero data files; 200k rows produced one Parquet file. Claim 2 above therefore depends
   on an explicit `ducklake_flush_inlined_data` step, which Lakehold exposes as a maintenance
   operation rather than leaving to chance.
2. **DuckLake's internal tables are visible in `information_schema`** alongside user tables
   (`ducklake_snapshot`, `ducklake_table`, …). The catalog explorer must filter them or the UI
   shows 20 internal tables per tenant.
3. **A rich maintenance surface exists** and becomes product features: `expire_snapshots`,
   `merge_adjacent_files`, `rewrite_data_files`, `cleanup_old_files`, `delete_orphaned_files`.
4. **CDC is built in** — `ducklake_table_changes`, `table_insertions`, `table_deletions` give
   change feeds between snapshots for free. MotherDuck does not expose this as directly.
5. **Time travel** via `ducklake_snapshots` and `AT (VERSION => n)`.

## Feature parity matrix

| Capability | MotherDuck | Lakehold | Notes |
|---|---|---|---|
| Serverless DuckDB SQL | ✅ | ✅ | Ducklings |
| Per-tenant isolation | ✅ hypertenancy | ✅ | Session-scoped attachment |
| Web SQL IDE | ✅ | ✅ | Monaco-based |
| Catalog explorer | ✅ | ✅ | |
| Open table format | ✅ DuckLake | ✅ DuckLake | Same format |
| Time travel | ✅ | ✅ | `ducklake_snapshots` |
| CDC / change feeds | ⚠️ limited | ✅ | `ducklake_table_changes` |
| Table maintenance | ✅ automatic | ✅ explicit, dry-run by default | We expose the knobs |
| Cross-catalog / shared reads | ✅ | ✅ | Read-only `AlsoAttach` |
| Query external files (S3/Parquet/CSV) | ✅ | ✅ | `httpfs` |
| Self-hosting | ❌ | ✅ | **Primary differentiator** |
| BYO bucket | ⚠️ paid tier | ✅ default | |
| .NET / EF Core integration | ❌ | ✅ | **Primary differentiator** |
| Dual execution (local+cloud) | ✅ | ❌ | Not replicated |
| Managed ingestion (Flights) | ✅ | 🛠️ roadmap | |
| AI / MCP server | ✅ | 🛠️ roadmap | MCP server is a natural fit |
| Data sharing | ✅ | ⚠️ partial | Read-only attach works; no share UI yet |
| Postgres wire endpoint | ✅ | 🛠️ roadmap | Unlocks all BI tools |

Legend: ✅ shipped · ⚠️ partial or gated · 🛠️ roadmap · ❌ not planned

## Roadmap

**Now** — SQL IDE, catalog explorer, query history, tenant/catalog CRUD, maintenance operations.

**Next** — Postgres wire-protocol endpoint (the single highest-leverage item: it unlocks Tableau,
Power BI, Metabase, and DBeaver in one stroke), MCP server for AI agents, scheduled maintenance,
read-only share links.

**Later** — read replicas for concurrent readers, managed ingestion connectors, semantic layer
generated from the EF Core model, row/column-level security.
