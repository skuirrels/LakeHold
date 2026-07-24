# Getting started with Lakehold

Lakehold is a self-hostable, multi-tenant lakehouse built on DuckDB and DuckLake. This guide takes
you from an empty checkout to a running warehouse, then walks every feature — what it does, how to
reach it, and what it is for.

This one file is the single source for both the in-app `/docs` page and the copy read on GitHub.

---

## Quick start

You need **Docker**, the **.NET 10 SDK**, and **Node.js 20 or newer**. The backing services —
PostgreSQL, MinIO, and a trace viewer — run in Docker; the API and the Angular dev server run either
on the host or in the same compose stack.

### 1. Clone and configure secrets

`.env` holds secrets only and is gitignored. Copy the checked-in template and fill in what it
documents.

```bash
git clone https://github.com/skuirrels/LakeHold.git
cd LakeHold
cp .env.example .env
```

### 2. Choose how to run it

**Everything in Docker.** One command brings up the backing services, the API, and the dev server.
The website is served at http://localhost:5399; the API is on `:5200`.

```bash
docker compose up
```

**Or — app on the host.** Start only the backing services in Docker, then run the two app processes
yourself. This is the faster inner loop for development.

```bash
docker compose up -d postgres minio minio-bucket jaeger   # backing services
dotnet run --project src/Lakehold.Api                      # API on :5200
npm start --prefix web/lakehold-ui                         # UI on :5399
```

Same URLs either way. The dev server proxies `/api` to `NG_API_URL`, which falls back to
`localhost:5200` when nothing sets it.

### 3. Open the workbench

Visit http://localhost:5399 and click **Open the workbench**. In development it ships with a seeded
demo catalog — a `demo` workspace with an `analytics` catalog of 250,000 events and 5,000 customers —
so there is something to run against before you load any data of your own.

### Build and test

```bash
dotnet build Lakehold.slnx        # restore + build every project
dotnet test Lakehold.slnx         # integration tests skip unless their service is configured
npm run build --prefix web/lakehold-ui
```

---

## The tools you'll use

There are four ways into a Lakehold catalog. They all resolve through the same tenant check, session
gate, and query history, so you can mix them freely.

| Tool | What it is | Best for |
|---|---|---|
| **The workbench** | A browser SQL IDE for exploring a catalog, running statements, browsing history and snapshots, and triggering maintenance. Ships seeded. | Exploration and operations. |
| **A Postgres client** | Lakehold speaks the PostgreSQL wire protocol, so `psql`, DBeaver, or Npgsql connect to a catalog with no driver or plugin. The user is the tenant and the database is the catalog. | Existing SQL clients and streamed results. |
| **.NET & EF Core** | Through `DuckDB.EFCoreProvider` your application model and your lake tables are one model. | .NET applications on the same schema. |
| **The HTTP API** | Minimal-API endpoints for queries, schemas, history, snapshots, maintenance, eject, backup/restore, and change-feed subscriptions. | Automation and integration. |

---

## The workbench, feature by feature

The browser workbench is the fastest way to explore a catalog and run maintenance. It is laid out top
to bottom: a workspace and catalog picker, a schema explorer on the left, a SQL editor, and a tabbed
output pane below it.

### Workspace and catalog pickers — *top bar*

A workspace is a tenant; a catalog is the isolated data unit attached to your session. Pick one of
each — every query, history entry, and maintenance run is scoped to that pair. Isolation comes from
which catalog is attached, not from anything in the SQL you submit.

### Catalog explorer — *left sidebar*

Browses the selected catalog's schemas, tables, and columns. Click an item to insert its name into
the editor, so you can build a query without retyping identifiers.

### SQL editor — *centre pane*

A plain, fast editor for arbitrary SQL. Press `⌘⏎` (`Ctrl+Enter`) or click **Run** to execute. Any
DuckDB/DuckLake statement is accepted — statements it does not specifically recognise fall back to
the ordinary streaming path rather than being refused.

### Results grid — *Output → Results*

Streams the result of the last statement into a grid, with the row count and elapsed time shown in
the toolbar. Integers and decimals beyond JavaScript's safe range are transported losslessly as
strings so nothing is silently rounded.

> Results are capped by `LakehouseOptions.MaxRowsPerResult` so a large query cannot exhaust memory.
> When the cap is hit you see a "Row limit reached" tag. The Postgres wire endpoint streams rows to
> the socket and does not apply the cap.

### Query history — *Output → History*

Every statement the workspace has run, successful or failed, with its row count, duration, and
timestamp. Click a row to replay it into the editor. Postgres-wire traffic shows up here too, so
client queries are visible for the first time.

### Snapshots and time travel — *Output → Snapshots*

Lists the catalog's DuckLake snapshots with their commit time and schema version. Each row shows the
`AT (VERSION => n)` clause to copy into a query, and a **Restore…** action that loads a ready-to-run
statement to roll a table back to that snapshot. See [Time travel](#time-travel) below for how
querying the past works and what you can do with it.

### Maintenance operations — *top bar → Maintenance*

Five one-click operations:

| Operation | What it does | Destructive? |
|---|---|---|
| **Flush** | Writes inlined commits out as Parquet. | No |
| **Compact** | Merges small Parquet files. | No |
| **Backup** | Exports the metadata catalog to Parquet beside the data. | No |
| **Expire** | Drops snapshots older than seven days. | **Yes — dry-run by default** |
| **Cleanup** | Deletes data files no longer referenced by any snapshot. | **Yes — dry-run by default** |

The two destructive operations return what they *would* do and change nothing until you click
**Apply for real**. Flush and Compact are the only operations that commit — they run inside a
transaction labelled `lakehold maintenance: …` so platform writes stay distinguishable from a
tenant's own.

---

## Time travel

Every write to a catalog — an `INSERT`, `UPDATE`, `DELETE`, a schema change, or a maintenance flush
or compaction — is committed as a **snapshot**: an immutable, numbered version of the whole catalog.
Nothing is overwritten in place, so every past state is still readable. Time travel is simply querying
a table as it was at one of those snapshots.

### How it works

DuckLake records a snapshot for each commit, identified by a monotonic **snapshot id** and stamped
with its commit time and schema version. Deletes and updates are stored as merge-on-read changes
rather than by rewriting data, so an earlier snapshot reconstructs exactly: the rows a later snapshot
deleted are still on disk, only masked at the current version. That is why history is real rather than
a convention — and why copying raw Parquet files resurrects deleted rows, which is what
[Eject](#eject-the-exit-path-in-one-call) exists to avoid.

### Finding a snapshot

Open the **Snapshots** tab in the workbench (Output → Snapshots). It lists every snapshot newest
first with its id, commit time, and schema version, and hands you the `AT (VERSION => n)` clause to
paste. The same history is available over the API at `GET …/snapshots`, and in SQL through the
catalog's `snapshots()` function:

```sql
SELECT snapshot_id, snapshot_time, schema_version
FROM analytics.snapshots()
ORDER BY snapshot_id DESC;
```

### Querying the past

Add an `AT (…)` clause to a table reference. By snapshot id:

```sql
-- The events table exactly as it was at snapshot 42
SELECT * FROM events AT (VERSION => 42);
```

Or by a point in time — DuckLake resolves it to whichever snapshot was current then:

```sql
-- The events table as of a moment
SELECT * FROM events AT (TIMESTAMP => TIMESTAMP '2026-07-20 09:00:00');
```

The clause attaches per table reference, so a single query can read two points at once. To see what a
later snapshot removed:

```sql
-- Rows that existed at snapshot 42 but are gone now
SELECT * FROM events AT (VERSION => 42)
EXCEPT
SELECT * FROM events;
```

Everything else is unchanged — joins, filters, aggregates, and `LIMIT` all work against the historical
version, and the read is never destructive.

### Rolling back a table

Time travel *reads* the past; to *return* a table to a past snapshot, rewrite it from that version:

```sql
-- Roll events back to its contents at snapshot 42
CREATE OR REPLACE TABLE events AS
SELECT * FROM events AT (VERSION => 42);
```

This records a new snapshot rather than erasing history, so the rollback is itself reversible and
earlier states stay reachable. In the workbench, the **Restore…** button on each row of the
Snapshots tab fills the editor with exactly this statement, ready to review and run. A snapshot
covers the whole catalog, so restore one table at a time; a table cannot be rolled back to a snapshot
from before it existed, and column constraints or defaults added since are not carried over.

> Prefer `CREATE OR REPLACE` over `DELETE` + `INSERT ... AT (VERSION => n)`. Reading a snapshot in the
> same transaction as a pending delete resolves the read *through* that delete and quietly restores
> the wrong rows.

### What it's good for

| Goal | How |
|---|---|
| Recover rows deleted by mistake | Read the old version and write the rows back: `INSERT INTO events SELECT * FROM events AT (VERSION => 41) WHERE …`. |
| Audit how a value changed | Query the same key across several snapshot ids. |
| Reproduce a past report | Run the report's query with `AT (TIMESTAMP => …)` set to the report date. |
| Diff two versions | `EXCEPT` or `INTERSECT` between two `AT (…)` reads. |

> **History is not kept forever.** Snapshots accumulate until you expire them. **Expire** (Maintenance)
> drops snapshots older than seven days and **Cleanup** deletes the files they alone referenced — both
> are irreversible and dry-run by default, so time travel to an old snapshot keeps working until you
> explicitly apply them. To carry history across an export, back up or eject *with the metadata
> catalog*; the data files alone recover the latest state only. See
> [Catalog backup and restore](#catalog-backup-and-restore).

---

## Data operations beyond the workbench

These are the features that make Lakehold a lakehouse rather than a query box. Today they are driven
over the HTTP API; the routes below all sit under
`/api/tenants/{tenant}/catalogs/{catalog}/`.

### Eject — the exit path in one call

`POST …/eject` · `GET …/ejects`

Re-materialises every table through the catalog into ordinary Parquet, plus the metadata catalog when
you want history. Because it re-reads rather than copying files, merge-on-read deletes are applied,
superseded update rows are gone, inlined commits are included, and DuckLake's internal columns never
leak. Every table is counted back through a plain Parquet reader and compared to the catalog before
the manifest is written; the manifest carries per-table row counts, SHA-256 digests, and an HMAC
signature when a key is configured.

> **Caveat.** A copy of the data path is *not* an eject — copying files resurrects deleted rows and
> duplicates updated ones. Eject is read-only and works on a read-only share, so it needs no flush
> first. See [`docs/EXIT-PATH.md`](https://github.com/skuirrels/LakeHold/blob/main/docs/EXIT-PATH.md)
> for the procedure eject automates.

### Catalog backup and restore

`GET …/backups` · `POST …/backups/restore`

The metadata catalog is exported to Parquet on a schedule and can rebuild a working catalog from that
export — row counts, deletions, updated values, views, and `AT (VERSION => n)` time travel all
intact. The backup root can be an `s3://` prefix. A catalog whose metadata lives in PostgreSQL
restores into a plain DuckDB file, so it is an exit path from the catalog database, not just a copy.

> **Caveat.** Restore never overwrites an existing catalog, and refuses an export with no completion
> manifest — a partial export missing `ducklake_delete_file` would silently reinstate deleted rows.

### Change data capture

`GET …/changes` · `…/subscriptions`

DuckLake already records what each snapshot changed, so Lakehold exposes it directly: a typed pull
API for change pages, and outbound webhooks fired per new snapshot and signed with HMAC-SHA256.
Updates arrive as a paired pre-image and post-image sharing a row id, so you can take net effect or
diff them. No Debezium, no Kafka, no second pipeline.

> **Caveat.** Delivery is at-least-once. The cursor advances one snapshot at a time and only after a
> 2xx, so a failing consumer replays rather than skips — make your handler idempotent on
> `(snapshot, row, change type)`.

### PostgreSQL wire endpoint

TCP `:5433` (off by default)

Connect any Postgres client straight to a catalog — the user is the tenant, the database is the
catalog. Statements resolve through the same tenant check, session gate, and query history as an HTTP
query. Results stream to the socket rather than being materialised, so the row cap does not apply.

> **Caveat.** `psql`, DBeaver, and Npgsql work today; Power BI does not yet (it reads a type
> catalogue DuckDB leaves empty). Enabling the port is a deliberate choice: it is off by default,
> with TLS and per-tenant credentials. See
> [`docs/POSTGRES-WIRE.md`](https://github.com/skuirrels/LakeHold/blob/main/docs/POSTGRES-WIRE.md).

---

## .NET and EF Core

Lakehold is built on `DuckDB.EFCoreProvider`, so your application model and your lakehouse tables can
be the same EF Core model — no transformation layer, no schema drift. The split is by whether the
workload has a known shape:

- **Modelled state** — tenants, catalogs, saved queries, and audit history — uses
  `ControlPlaneContext` on native DuckDB, which needs sequences and `RETURNING`.
- **Arbitrary result shapes** — anything you query against a lake catalog — stream through the
  provider's dynamic path, capped and cancellable, without inventing fake entity types.

A first-class client package with a typed `ChangeEvent<T>` change stream is on the roadmap but not
shipped yet — the .NET story today is a property of the architecture and the provider, not something
you can add to a `csproj`.

---

## Authentication

Lakehold authenticates with **API tokens**. A token names one tenant, may be narrowed to a single
catalog, carries a role (`owner`, `editor`, `reader`), and can be revoked — which closes the HTTP API
and the PostgreSQL wire endpoint together.

**Enforcement is opt-in, and off by default.** Until you turn it on, a request with no token still
works and is trusted to name its own tenant — which keeps local development frictionless and is
exactly what you must not deploy. To close the door:

```json
{ "Lakehold": { "Auth": { "RequireAuthentication": true } } }
```

### Getting your first token

A node with no tokens mints one on start-up, logs it **once**, and never again. It is instance-scoped:
it provisions tenants, catalogs, and other tokens, but deliberately cannot read data — so a leaked
admin credential is a visible provisioning problem rather than a silent data breach.

```bash
docker compose -f compose.production.yaml up -d          # read the bootstrap token from the log

curl -X POST …/api/tenants        -H 'Authorization: Bearer lkh_admin_…' -d '{"slug":"acme","displayName":"Acme"}'
curl -X POST …/api/tenants/acme/catalogs -H 'Authorization: Bearer lkh_admin_…' -d '{"name":"analytics"}'
curl -X POST …/api/tenants/acme/tokens   -H 'Authorization: Bearer lkh_admin_…' -d '{"name":"bi","role":"reader"}'
```

Set `Lakehold__BootstrapToken` if your platform injects credentials and cannot scrape a log.

A token is shown once at creation and stored only as a SHA-256 hash, so it cannot be recovered — from
the API or the database. Reaching a tenant or catalog your credential does not name returns **404**,
not 403: a 403 would confirm it exists.

### Capability comes from attachment

A `reader` token does not get a permission check that clever SQL might route around — its catalog is
attached **read-only**, so a write fails in the engine itself. That is the same reasoning behind
Lakehold's isolation model: a session can only reference the catalog attached to it.

Humans can sign in with **OIDC** instead (Keycloak, Entra, Authentik, Auth0). Configure an authority
and a tenant claim; leave it unset and the whole path stays off, so an air-gapped install never takes
a dependency on an identity provider. The full design is in
[`docs/AUTHENTICATION.md`](https://github.com/skuirrels/LakeHold/blob/main/docs/AUTHENTICATION.md).

---

## Reference

The design docs go deeper than this guide.

| Document | Covers |
|---|---|
| [`docs/ARCHITECTURE.md`](https://github.com/skuirrels/LakeHold/blob/main/docs/ARCHITECTURE.md) | Architectural rationale and current product boundaries. |
| [`docs/EXIT-PATH.md`](https://github.com/skuirrels/LakeHold/blob/main/docs/EXIT-PATH.md) | The verified open-format exit procedure that eject automates. |
| [`docs/POSTGRES-WIRE.md`](https://github.com/skuirrels/LakeHold/blob/main/docs/POSTGRES-WIRE.md) | The wire protocol surface and what is deliberately unimplemented. |
| [`docs/AUTHENTICATION.md`](https://github.com/skuirrels/LakeHold/blob/main/docs/AUTHENTICATION.md) | The phased plan for API authentication and tenant identity. |
| [`docs/PROVIDER-FEEDBACK.md`](https://github.com/skuirrels/LakeHold/blob/main/docs/PROVIDER-FEEDBACK.md) | Provider capabilities and why the data plane uses its dynamic API. |
| [`README.md`](https://github.com/skuirrels/LakeHold/blob/main/README.md) | Build, run, and the full set of environment variables and test commands. |
