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
| Zero operations | You run it. We ship containers and compose, but you own uptime. |
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

Established later, on DuckDB 1.5.4, while building eject and CDC:

6. **`ducklake_table_changes` is inclusive at both ends.** A range of `[2, 4]` includes snapshot 2's
   own changes; `[3, 4]` does not. So a poller resuming after snapshot `L` must open the next window
   at `L + 1`, and the change feed's shape is `snapshot_id, rowid, change_type` followed by the
   table's own columns. An update is *two* rows — `update_preimage` and `update_postimage` sharing a
   `rowid` — not one row with before/after columns.
7. **A range whose END predates a table's creation raises**, while a range whose *start* predates it
   is fine and simply yields nothing until the table exists. The dispatcher never trips this because
   its end bound is always the latest snapshot, but a caller passing arbitrary bounds can.
8. **`COPY (SELECT * FROM table)` is the correct exit primitive.** It applies merge-on-read deletes,
   collapses superseded update rows, includes data still inlined in the metadata catalog, and emits
   none of the `_ducklake_internal_*` columns — verified by reading the result on a connection where
   the `ducklake` extension was never loaded. Copying the data path's files directly does none of
   these things, which is why the naive glob in `docs/EXIT-PATH.md` is documented as wrong.

## Competitive landscape

Lakehold's category is "self-hostable open-format lakehouse." That places it against three groups
of competitors, and the honest positioning differs for each.

- **MotherDuck** — the closest peer: same engine (DuckDB), same table format (DuckLake), opposite
  hosting model. Every argument against it is a hosting/integration argument, never a capability
  one. It is years ahead on UI polish, managed ingestion, and dual local↔cloud execution.
- **Managed lakehouse incumbents — Databricks, Snowflake.** Enormously more capable engines with
  elastic scale-out, mature governance (Unity Catalog, Horizon), marketplaces, and AI features
  (Genie, Cortex). Neither is self-hostable: Snowflake has no on-prem story, and Databricks keeps a
  vendor-hosted control plane even when compute runs in your VPC. Both have moved toward open
  formats (Delta/UniForm, Iceberg via Polaris), which *weakens* the "open format" moat but not the
  "runs entirely in your environment" or ".NET-native" ones.
- **Self-hostable open stacks — Dremio, and the DIY Iceberg assembly** (Trino/Spark + a REST
  catalog such as Nessie, Polaris, or Lakekeeper + a BI layer such as Superset). These share
  Lakehold's self-host + open-format ground, so differentiation here is *operational simplicity*
  (one .NET service and a DuckDB file vs. a JVM cluster and a separate catalog service), single-node
  efficiency, and the .NET/EF Core model integration none of them offer. Dremio Community is the
  sharpest direct competitor: self-hostable, Iceberg-based, catalog branching via Nessie, a web UI,
  and Arrow Flight — but JVM-heavy, cluster-oriented, and not application-integrated.
- **Self-host OLAP alternatives — ClickHouse, StarRocks, Apache Doris.** Extremely fast and
  self-hostable, but built on their own storage engines (MergeTree, etc.), not an open lakehouse
  table catalog. They can *read* Iceberg/Delta externally, but the durable table format is
  proprietary to the engine — the opposite of Lakehold's "every byte is Parquet you can read without
  us" guarantee. Relevant as raw-speed alternatives, not as open-format lakehouses.

## Feature / capability matrix

| Capability | MotherDuck | Databricks | Snowflake | Dremio (self-host) | DIY Iceberg stack | Lakehold |
|---|---|---|---|---|---|---|
| Runs entirely in your infra / air-gapped | ❌ | ❌ hosted control plane | ❌ | ✅ | ✅ | ✅ |
| BYO object store as default | ⚠️ paid | ⚠️ | ❌ | ✅ | ✅ | ✅ |
| Open table format, readable without the vendor | ✅ DuckLake | ⚠️ Delta/UniForm | ⚠️ Iceberg | ✅ Iceberg | ✅ Iceberg | ✅ DuckLake/Parquet |
| No proprietary metadata catalog required to read | ✅ | ❌ Unity | ❌ Polaris/Horizon | ⚠️ Nessie | ⚠️ REST catalog | ✅ plain SQL catalog |
| Verified, attested exit path as a product feature | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ **unique** |
| Serverless-feel / auto-suspend compute | ✅ | ✅ | ✅ | ⚠️ | ❌ | ✅ Ducklings idle-evict |
| Elastic scale-out (multi-node) | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ single-writer node |
| Per-tenant isolation as a product primitive | ✅ hypertenancy | ✅ | ✅ | ⚠️ projects | ❌ DIY | ✅ session attachment |
| Web SQL IDE | ✅ mature | ✅ | ✅ | ✅ | ❌ add Superset | ✅ Monaco, focused |
| Catalog explorer | ✅ | ✅ | ✅ | ✅ | ⚠️ | ✅ |
| Time travel / snapshots | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| CDC / change feeds | ⚠️ limited | ✅ CDF | ✅ streams | ⚠️ | ⚠️ | ✅ typed feed + webhooks |
| CDC without a separate pipeline (no Debezium/Kafka) | ⚠️ | ❌ | ❌ | ❌ | ❌ | ✅ **unique** |
| Catalog branching (git-style) | ❌ | ⚠️ | ❌ | ✅ Nessie | ✅ Nessie | 🛠️ roadmap |
| Table maintenance controls | ✅ automatic | ✅ auto | ✅ auto | ✅ | ⚠️ manual | ✅ explicit, dry-run default |
| Query external files (S3/Parquet/CSV) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ `httpfs` |
| Data sharing | ✅ | ✅ Delta Sharing | ✅ mature | ✅ | ⚠️ | ⚠️ read-only attach, no UI |
| Postgres / BI wire protocol | ✅ | ✅ JDBC | ✅ | ✅ Flight/JDBC | ✅ Trino JDBC | 🛠️ roadmap |
| Managed ingestion connectors | ✅ Flights | ✅ | ✅ | ⚠️ | ❌ | 🛠️ roadmap |
| AI / MCP / assistant | ✅ | ✅ Genie | ✅ Cortex | ⚠️ | ❌ | 🛠️ roadmap |
| Row / column-level security | ✅ | ✅ | ✅ | ✅ | ⚠️ | 🛠️ later |
| Dual local↔cloud hybrid execution | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| .NET / EF Core model integration | ❌ | ❌ | ⚠️ connector only | ❌ | ❌ | ✅ **unique** |
| Cost model | per-sec compute | DBU credits | credits | node + support | node + ops | VM + bucket |
| Open source / self-managed licence | ❌ | ⚠️ UC OSS only | ❌ | ⚠️ Community ed. | ✅ | ✅ |

Legend: ✅ shipped/strong · ⚠️ partial, gated, or connector-only · 🛠️ roadmap · ❌ not available

**Reading the matrix.** No competitor holds *all three* of {runs entirely in your infra, table data
readable with no vendor catalog, .NET/EF Core model integration}. MotherDuck matches the format and
the serverless feel but not the hosting or .NET story. Dremio and the DIY stack match the hosting
and openness but not the operational simplicity or .NET story. The incumbents dominate on scale and
governance but fail the hosting and .NET tests outright. The defensible wedge is the *intersection*,
not any single row.

## Differentiators built on that intersection

Two of the three capabilities identified as defensible USPs are now shipped. Both lean on the
intersection above — neither is copyable by a competitor without abandoning their own model, because
a hosted vendor **is** the lock-in it would have to prove an exit from, and a JVM lakehouse cannot
offer a typed .NET change stream.

### 1. Verified eject bundles — shipped

A one-call, reader-agnostic export that turns the tested exit path into an auditable artifact rather
than a procedure someone has to follow correctly under pressure.

The hard part is that a naive copy of the data path is **wrong**. DuckLake deletes are merge-on-read
sidecars, updates leave superseded rows in place, and small commits may be inlined into the metadata
catalog and never written to Parquet at all. So an eject does not copy files — it re-materialises
each table *through* the catalog with `COPY (SELECT * FROM table) TO …`, which applies deletions and
updates, includes inlined rows, and drops the internal `_ducklake_internal_*` columns.

Each written file is then counted back through the plain Parquet reader and compared against the
catalog's own count. Any disagreement aborts the eject **before** a manifest is written, so an
unverified bundle can never present itself as complete. The manifest records per-table row counts and
SHA-256 digests and, when `EjectSigningKey` is set, an HMAC-SHA256 signature over exactly those
facts — making the attestation tamper-evident rather than merely present.

Because it only reads, an eject never mutates the catalog and works against a read-only share. It
does not need a flush first, unlike a raw copy of the data path.

- **Engine**: `Lakehold.Engine/Catalog/CatalogEject.cs`, `MetadataExporter.cs`
- **API**: `POST /api/tenants/{tenant}/catalogs/{catalog}/eject`, `GET …/ejects`
- **Verified**: exported Parquet reads back correctly on a connection where the `ducklake` extension
  was never loaded; digests and the HMAC recompute independently outside .NET entirely.

### 2. Debezium-free CDC — shipped

DuckLake already records what each snapshot changed. Lakehold turns that into two subscribe-able
surfaces without a second pipeline: a **pull** API returning typed change pages, and **push**
webhooks fired per new snapshot.

`ducklake_table_changes(catalog, schema, table, start, end)` is inclusive at both ends — verified on
DuckDB 1.5.4 — so a consumer that has processed through snapshot `L` reads the next window from
`L + 1` and no change is delivered twice. Updates arrive as a paired `update_preimage` /
`update_postimage` sharing a `rowid`, so a consumer can take net effect or diff the two.

Deliveries advance **one snapshot at a time**, and the cursor moves only after a 2xx. That makes the
contract at-least-once with a resumable cursor: a crashed or failing consumer replays from where it
stopped rather than skipping a window. Payloads are HMAC-SHA256 signed (`X-Lakehold-Signature`,
GitHub/Stripe-style) with a timestamped signing base, so a receiver can reject both forgeries and
replays. A failing endpoint backs off exponentially rather than retrying every poll.

The row cap is deliberate: a webhook is a *notification with the common case inlined*, not a bulk
transfer channel. A window larger than the cap sets `truncated` and the consumer pulls the remainder
from the changes API — one large backfill cannot wedge every consumer behind it.

- **Engine**: `Lakehold.Engine/Catalog/ChangeFeed.cs`
- **Control plane**: `ChangeSubscription` (secret stored for signing, never returned or logged)
- **API**: `GET …/changes`, `GET|POST …/subscriptions`, `DELETE …/subscriptions/{id}`

### 3. Embedded Duckling — not built

Package the Duckling/`LakeContext` path as a library so a .NET app runs a single-tenant lakehouse
in-process (edge, desktop, air-gapped appliance, integration tests), then points identical code at a
Lakehold server with no query changes. This is the self-hostable answer to MotherDuck's dual
local↔cloud execution, and it is only possible because the data plane is already a model-less .NET
`DbContext` rather than a JVM cluster. Would need to keep the isolation boundary structural
(invariant 4) and the writer serialised (invariant 5) in-process.

## Roadmap

**Now** — SQL IDE, catalog explorer, query history, tenant/catalog CRUD, maintenance operations,
verified eject bundles, CDC pull API and signed webhooks.

**Next** — Postgres wire-protocol endpoint (the single highest-leverage item: it unlocks Tableau,
Power BI, Metabase, and DBeaver in one stroke), MCP server for AI agents, read-only share links.

**Later** — embedded Duckling, read replicas for concurrent readers, managed ingestion connectors,
semantic layer generated from the EF Core model, row/column-level security.
