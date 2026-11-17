# Lakebase — an operational store beside the lake

Databricks shipped **Lakebase** in June 2025 and took it to general availability on AWS during 2026:
managed PostgreSQL inside the lakehouse, built on the Neon engine it acquired the same year. The
category claim is broader than the product — an OLTP database built on lakehouse principles, with
storage separated from compute, object storage as the source of truth, cheap history, and native
integration with the analytical tables sitting next to it.

Like [`AUTHENTICATION.md`](AUTHENTICATION.md), [`PUBLIC-API.md`](PUBLIC-API.md), and
[`MCP.md`](MCP.md), this is a specification and a running record, written to be worked one step at a
time. Nothing here contradicts an invariant in `AGENT.md`; where a rule already exists, this document
says how the surface preserves it rather than restating why.

**Status: not built. This document is the proposal.** No entity, endpoint, or adapter described
below exists yet. The claim is carried publicly as *planned* — an `Operational (OLTP) store` row in
the `README.md` and `/compare` matrix with its evidence contract in
`web/lakehold-ui/e2e/support/compare-capabilities.ts`, a `🛠️ specified, unstarted` cell in the
[`ARCHITECTURE.md`](ARCHITECTURE.md) capability matrix, and P1.7 in
[`ENTERPRISE-DATA-PLATFORM-ROADMAP.md`](ENTERPRISE-DATA-PLATFORM-ROADMAP.md). Those four say
*planned*; if any of them ever says *shipped* before this document's phases land, the matrix is
lying and the browser suite is the thing that should catch it. Competitor claims date from when
this document was written, in August 2026, and have not been re-checked since; treat them the way
[`COMPETITIVE-RESEARCH.md`](COMPETITIVE-RESEARCH.md) asks its own claims to be treated.

## What a lakebase is

Five properties do the work. They are worth naming separately, because LakeHold can reach four of
them honestly and cannot reach the fifth without adopting somebody else's engine.

| Property | What the category means by it |
|---|---|
| Postgres-compatible OLTP | Real transactional PostgreSQL — ACID, single-digit-millisecond reads, high concurrent write serving. Not the analytics engine impersonating one. |
| Separated storage and compute | Stateless elastic compute over durable object storage; autoscaling, and scale-to-zero on an idle timeout. |
| Instant branching | Branches are copy-on-write views over shared storage. A full database copy in seconds, costing nothing until it diverges, plus cheap point-in-time restore. |
| Bidirectional incremental sync | Analytical tables are pushed into Postgres from the change feed — changed rows only, no full reload — and operational tables are readable as analytical ones without a separate pipeline. |
| One governance model | The operational database is a catalog object. Same identity, same access control, same audit trail across OLTP and OLAP. |

The workloads that follow from it: application and session state, agent state and chat history,
online feature stores, and serving computed analytical results back to an application at a latency
the lake cannot offer.

## Why this, and why now

[`AVRO-AND-UPDATE-HEAVY-EDP.md`](AVRO-AND-UPDATE-HEAVY-EDP.md) already reached the conclusion this
plan builds on, and it reached it from the opposite direction:

> If the requirement is sustained row-at-a-time OLTP, sub-second synchronous entity reads and high
> concurrent write serving, the operational database should remain the serving authority. LakeHold
> should consume its change log and provide governed analytical state rather than being forced into
> an OLTP role by a serialization-format decision.

That is not an argument against this feature. It **is** the lakebase thesis, stated as a warning
instead of a product. The category's answer to the same problem is: do not make the analytical
engine serve rows, put a real PostgreSQL beside it, and govern the two as one thing. The existing
document says what LakeHold should refuse to become; this one says what to build so the refusal
stops being a gap.

The commercial reading is the same as everywhere else in the product. Databricks' version requires
their workspace, their storage, and their catalog. LakeHold's version is your Postgres, your bucket,
open format on both halves, and no egress. The differentiator is not that LakeHold has a lakebase.
It is that the operational store is one you already run and can keep.

## What LakeHold already has

More of the pattern is built than the framing suggests, and the hard half is the built half.

| Lakebase capability | LakeHold today |
|---|---|
| Change data feed to drive sync | **Shipped.** `ChangeFeed` reads `ducklake_table_changes` as typed `Insert` / `Delete` / `UpdatePreimage` / `UpdatePostimage` rows with a stable `RowId` pairing each update, a resumable cursor, and page truncation. This is the Delta CDF equivalent and it is the input a synced table needs. |
| Consistent initial load | **Shipped, unrecognised.** DuckLake time travel means a full load can read *at* a named snapshot while writers continue, so the seed and the first incremental window meet exactly, with no lock and no gap. |
| Point-in-time restore | **Shipped for the analytical half.** Snapshots plus `TableRestore`. |
| Operational database → lake | **Shipped.** `PostgreSqlDataConnectorSource` with durable proposed/current checkpoints, keyed-delta validation, idempotent replay, and a publication fence. |
| Credential discipline | **Shipped.** Storage profiles persist a *name*; the secret stays in deployment configuration (invariants 8 and 13). A lakebase profile is the same shape. |
| One authorization model | **Shipped.** `TenantMember` owns authorization independently of the identity provider; routes declare a `Capability` and `LakeholdAuthorizationFilter` enforces it in one place (invariants 19 and 21). |
| Run history and scheduling | **Shipped.** `DataConnector`, `DataConnectorRun`, the connector worker, triggers, and schedules. |
| PostgreSQL as an operational dependency | **Already required** twice over — control plane and DuckLake metadata. This plan adds a role for it, not a dependency. |
| Lake → operational database | **Missing.** This is the feature. |
| The database as a catalog object | **Missing.** |
| Branching | **Missing on both halves.** The analytical half is specified in [`BRANCHING-PLAN.md`](BRANCHING-PLAN.md). |
| Autoscaling, scale-to-zero | **Out of reach on stock PostgreSQL. Not claimed.** |

## Product decision

Build the **serving direction and the governance around it**. Do not build a database engine.

LakeHold registers, governs, seeds, and synchronises an operational PostgreSQL database; it does not
sit in that database's hot path. An application connects to Postgres directly, with a credential
LakeHold issued, against tables LakeHold keeps current from the lake. The API's involvement ends at
provisioning, synchronisation, credentials, and audit.

That boundary is what makes the feature small enough to be real. It also keeps every claim
defensible: LakeHold is not asserting a millisecond latency it does not control, it is asserting that
the rows are correct, that the sync is incremental and resumable, and that access to both halves is
decided in one place.

## Where it lives

- `src/Lakehold.ControlPlane/Model/Entities.cs` — `Lakebase`, `SyncedTable`, `SyncedTableRun`, in the
  modelled EF Core context with a migration (invariants 1 and 2).
- `src/Lakehold.Api/Lakebases/` — endpoints, options, the sink adapter, and the worker, following the
  precedent `Connectors/`, `Cdc/`, `PgWire/`, and `Mcp/` already set: a surface inside the API project
  rather than a project of its own. One process, one credential store, one audit trail.
- `src/Lakehold.Engine` — unchanged except for whatever read helper the seed path needs.
  **The engine must not learn about PostgreSQL targets.** Its job ends at producing rows and changes.

## Object model

```text
Tenant
 └── Lakebase                     name, provisioning mode, profile name, placement, status
      └── SyncedTable             source catalog/schema/table → target schema/table
           └── SyncedTableRun     status, rows, window, error — mirrors DataConnectorRun
```

`Lakebase` carries a **profile name and a placement**, never a credential. This is invariant 8 and 13
applied to a different resource: the record holds `ProfileName`, `Host`, `Port`, and `Database`, all
of which are safe to display; the DSN and password live in `Lakehold:Lakebases:Profiles:<name>` in
deployment configuration or its secret store, exactly as an object-store profile does. A profile name
in a response is fine. A password in one is a bug.

Two provisioning modes, and the difference is only who creates the database:

- **Registered** — an operator names a profile that already points at a database. The realistic
  default for a self-hosted product, and the only mode that works when the operational database is
  somebody else's.
- **Managed** — LakeHold creates a database and an owning role on the cluster the profile names. The
  generated role password is returned **once** and never persisted, on the same reasoning that makes
  the bootstrap token invariant 19's single logged-credential exception. Phase 5 replaces show-once
  with on-demand short-lived credentials, which is what the category actually does.

`SyncedTable` carries `LastPublishedSnapshot` — a DuckLake snapshot id, not a timestamp and not an
opaque adapter string. The cursor is the same thing the change feed is addressed by, which is what
makes the whole contract below fall out.

## Synced tables — the contract

### Seed

1. Read `ChangeFeed.LatestSnapshotAsync` **first** and hold it as `S`.
2. Stream the table as of `S` through the engine's dynamic path and load it into Postgres with an
   Npgsql binary import.
3. Commit `LastPublishedSnapshot = S`.

Taking `S` before reading is not a detail; it is the whole reason the seed needs no lock. DuckLake
answers the read at `S` however long it takes, and the first incremental window opens at `S + 1`, so
no change can fall between the two. A seed that read first and asked for a snapshot afterwards would
have a gap exactly the width of the load.

### Incremental

Each run reads `[LastPublishedSnapshot + 1, latest]`. The `+ 1` is invariant 18: `ducklake_table_changes`
is inclusive at both ends, so the next window opens one past the last published snapshot or the
boundary snapshot is applied twice.

Within the window, changes are **collapsed per key** before anything is written — last write wins
across the range, so a row touched forty times in forty snapshots produces one upsert, not forty.
The collapse is what makes this cheaper than replaying the feed:

| Feed row | Effect on the collapsed set |
|---|---|
| `Insert`, `UpdatePostimage` | Upsert at that key |
| `Delete` | Delete at that key |
| `UpdatePreimage` | Ignored, **except** when its key differs from the paired postimage's — a key change is a delete of the old key and an insert of the new. `RowId` pairs the two. |
| `Unknown` | Fails the run. A change type this build does not recognise must not be silently dropped from a target that claims to be current. |

The result applies in **one PostgreSQL transaction**: `DELETE` by key, `INSERT … ON CONFLICT (key)
DO UPDATE`, and the new `LastPublishedSnapshot` written in the same transaction as the rows it
describes.

That last clause is worth stating loudly, because it is stronger than anything else in the product.
CDC delivery over webhooks is at-least-once and must be, since an HTTP consumer cannot commit its
cursor with its data (invariant 18). A synced table's consumer is a transactional database, so it
can — and the sync is therefore **exactly-once at the target**, with replay of an interrupted run
producing no duplicate and no gap. Do not weaken this by advancing the cursor in a second
transaction, or the feature inherits a guarantee it did not have to.

`ChangeFeedPage.Truncated` and `NextCursor` resume within a window; a run that truncates commits the
part it applied only if the collapse is closed at a snapshot boundary, otherwise it carries forward.

### Schema

Column types map DuckDB → PostgreSQL through one table, and drift is governed by the same three
policies the connector platform already defines — `reject`, `additive`, `mapped-version`. Do not
invent a fourth vocabulary for the same problem.

### Modes

`Snapshot` (re-seed each run), `Incremental` (the contract above), and `Continuous` (incremental on
a short schedule). Scheduling, triggers, run history, and failure reporting reuse the connector
worker rather than growing a parallel one.

## Branching

Split this honestly, because the two halves are not the same difficulty and one of them is a genuine
differentiator that belongs in LakeHold whether or not this plan proceeds.

**Analytical branching is cheap, and it has its own plan.** DuckLake metadata is ordinary SQL in a
per-catalog schema and Parquet data files are immutable, so a branch is a copy of metadata rows
referencing the same files: O(metadata), seconds, and no data movement.
[`BRANCHING-PLAN.md`](BRANCHING-PLAN.md) specifies it, with the engine spikes behind each rule, and
does not wait for anything here. It also answers the deletion problem this document first raised —
two schemas referencing one file set make "the catalog" no longer singular, so cleanup driven by one
schema's references could delete files the other still needs. Rather than counting references across
schemas before shipping, it arranges that no two schemas share a data path either can delete from: a
sibling branch root, a fork-snapshot pin on the parent, and a branch that refuses `expire` and
`cleanup`. Counting is that plan's later phase.

What remains here is the operational half.

**PostgreSQL branching cannot be done on stock PostgreSQL**, and the plan should say so rather than
approximate it:

| Approach | Reality |
|---|---|
| `CREATE DATABASE … TEMPLATE` | Works, is a real copy, and blocks connections to the template while it runs. Acceptable for a small serving database; not "instant". |
| Filesystem snapshot (ZFS, Btrfs) | Genuinely cheap, self-hosted only, and outside anything LakeHold can portably orchestrate. Document it; do not automate it. |
| Neon as a provisioning mode | The only route to actual copy-on-write branching and scale-to-zero. Neon is Apache-2.0 and self-hostable, which fits the licence position, and it is the engine the category is defined by. |

Recommendation: template-copy for stock PostgreSQL, a first-class Neon provisioning mode for
deployments that want the real thing, and **no scale-to-zero claim anywhere**.

## Identity and governance

This is the part that makes it a lakebase rather than "we also provision a database".

One `TenantMember` row decides reach across both planes. A member who cannot reach a catalog cannot
reach a lakebase seeded from it, and the refusal keeps invariant 19's shape — subject before
capability, 404 rather than 403 for an unreachable subject, because a 403 confirms it exists.

Credentials are minted, not stored. The existing token machinery already issues scoped, hashed,
prefix-identified credentials; Phase 5 extends it to issue **short-lived PostgreSQL roles** for an
application or an agent, which is the same move Databricks makes with workspace OAuth tokens. Until
then, managed mode is show-once and registered mode uses the deployment's own credential.

Every provisioning, sync, and credential-issue action writes to the existing audit history. One
trail, not a second one that happens to be about Postgres.

## Delivery phases

Each phase is independently shippable and leaves the product working.

### Phase 1 — registration and identity
Entities, migration, `Capability`-declaring endpoints, audit rows, redacted read model, Workbench
list and detail. Registered mode only. No data moves. Ships the object model and proves the
credential boundary before anything depends on it.

### Phase 2 — synced tables, seed only
`Snapshot` mode: snapshot-first read, binary import, `LastPublishedSnapshot` committed with the data.
Run history reuses the connector run model. This is already useful on its own — a nightly serving
copy of a computed table.

### Phase 3 — synced tables, incremental
The change-feed contract above: `+ 1` windows, per-key collapse, single-transaction apply, truncation
resume, schema policies. The exactly-once property lands here and should be tested as a first-class
claim, not inferred.

### Phase 4 — the return path
A registered lakebase becomes eligible as a `DataConnector` source automatically, so one registration
gives both directions and the lake table is the governed analytical mirror of the operational one.
Mostly wiring; `PostgreSqlDataConnectorSource` already does the work.

### Phase 5 — credentials on demand
Short-lived PostgreSQL roles issued through the existing token machinery, replacing show-once.
Governance parity is claimable from here and not before.

### Phase 6 — operational branching
Template-copy for stock PostgreSQL, then a Neon provisioning mode. Analytical branching is
[`BRANCHING-PLAN.md`](BRANCHING-PLAN.md) and does not wait for this phase.

### Phase 7 — surfaces
MCP tools with the usual independent write and operator gates (invariant 21), public API and SDK
coverage per [`PUBLIC-API.md`](PUBLIC-API.md), and the `/docs` page. The compare-matrix row already
exists as *planned*; this phase is where its claim and evidence change, and not before.

## Test and acceptance matrix

| Area | Required evidence |
|---|---|
| Seed consistency | Concurrent writes during a seed; the first incremental window applies exactly the changes the seed did not see |
| Window arithmetic | A change in the boundary snapshot appears once, not twice and not never |
| Collapse | N updates to one key across N snapshots produce one upsert; a key change produces a delete and an insert |
| Exactly-once | Kill the worker mid-apply, at commit, and after commit; replay produces an identical target |
| Deletes | Merge-on-read deletes and update-preimage rows reach the target correctly |
| Unknown change type | Fails the run; does not silently drop |
| Truncation | A window exceeding the page ceiling resumes without duplication |
| Schema drift | `reject`, `additive`, and `mapped-version` behave as the connector platform defines them |
| Authorization | Reader refused write; a member without catalog reach gets 404, not 403; cross-tenant lakebase names do not collide |
| Redaction | No DSN, password, or profile secret in any response, log, error, UI state, or control-plane row |
| Regression | Full backend, frontend, PostgreSQL integration, and Workbench E2E suites pass |

## Deliberate non-goals

- **DuckDB does not become an OLTP engine.** Invariant 5 makes a Duckling a single-writer serialised
  session. That is not a limitation to route around; it is why the lakebase exists.
- **The PostgreSQL wire endpoint is not the lakebase.** [`POSTGRES-WIRE.md`](POSTGRES-WIRE.md)
  describes an analytical read surface that speaks the protocol. Conflating the two would promise
  transactional semantics the engine behind it does not have.
- **Application traffic does not pass through the API.** LakeHold governs provisioning, sync, and
  credentials. It is not a connection pooler.
- **No application database password is persisted in the control plane.**
- **No scale-to-zero or autoscaling claim** outside a Neon provisioning mode.
- **No claim of Unity Catalog parity.** LakeHold has one authorization model across two planes, which
  is the useful half; it does not have lineage, row policies, or column masking, and
  [`ARCHITECTURE.md`](ARCHITECTURE.md) already says so.

## Implementation pointers

- `src/Lakehold.Engine/Catalog/ChangeFeed.cs` — `LatestSnapshotAsync`, `ReadAsync`, `TableChange`,
  `ChangeType`, `ChangeFeedPage.Truncated` / `NextCursor`. The entire sync contract is expressed in
  these types; nothing new is needed from the engine.
- `src/Lakehold.Api/Connectors/IDataConnectorSource.cs` — mirror it. `IDataConnectorSink` with a
  `ConnectorAdapterManifest`-shaped manifest, a write context carrying tenant and catalog as explicit
  subjects, and a runtime that commits the cursor rather than letting the adapter do it.
- `src/Lakehold.Api/Connectors/ConnectorRunner.cs`, `ConnectorWorker.cs` — scheduling, triggers,
  run status, and failure reporting to reuse rather than reimplement.
- `src/Lakehold.ControlPlane/Model/Entities.cs` — `DataConnector`, `DataConnectorRun`, and
  `ChangeSubscription` are the models to follow for shape, status vocabulary, and naming.
- `src/Lakehold.ControlPlane/Security/Capability.cs` — `TenantWrite` for sync administration,
  `TenantOwner` for provisioning and credential issue.
- [`POSTGRES-AND-STORAGE.md`](POSTGRES-AND-STORAGE.md) — the profile pattern to copy exactly. Do not
  restate its configuration rules here; link to it.

## Sources

Checked when this document was written; re-gather rather than amend when it ages, per the standard
[`COMPETITIVE-RESEARCH.md`](COMPETITIVE-RESEARCH.md) sets.

- Databricks — *What is Lakebase Postgres?* <https://developers.databricks.com/docs/lakebase/overview>
- Databricks — *Databricks Lakebase is now Generally Available* <https://www.databricks.com/blog/databricks-lakebase-generally-available>
- Databricks — *Reverse ETL with Lakebase* <https://www.databricks.com/blog/reverse-etl-lakebase-activate-your-lakehouse-data-operational-analytics>
- Databricks — *Serve lakehouse data with synced tables* <https://docs.databricks.com/aws/en/oltp/instances/sync-data/sync-table>
- Neon — *Neon and Lakebase* <https://neon.com/docs/introduction/neon-and-lakebase>
