# Getting started with LakeHold

LakeHold is a self-hostable, tenant-aware lakehouse built on DuckDB and DuckLake. This guide takes
you from an empty checkout to a running warehouse, then walks every feature — what it does, how to
reach it, and what it is for. Tenant identity and credentials are scoped today; the current
node-local session and artifact layout is for trusted evaluation or a one-tenant production profile,
not shared adversarial multi-tenancy when tenants reuse a catalog name. See the
[production-readiness roadmap](https://github.com/skuirrels/LakeHold/blob/main/docs/PRODUCTION-READINESS-ROADMAP.md).

Running a production node is a separate discipline from learning the features. Operators should
start with the [operations hub](https://lakehold.dev/docs/operations),
then assign and test the linked incident-response, disaster-recovery, and monitoring runbooks before
accepting traffic.

This one file is the single source for both the in-app `/docs` page and the copy read on GitHub.

---

## Quick start

You need **Docker**. The **.NET 10 SDK** and **Node.js 20 or newer** are needed only if you run the
API and the Angular dev server on the host — the all-in-Docker path below compiles both inside the
stack. Either way the backing services (PostgreSQL, MinIO, and a trace viewer) run in Docker.

### 1. Clone and configure secrets

`.env` holds secrets only and is gitignored. Copy the checked-in template and fill in what it
documents.

```bash
git clone https://github.com/skuirrels/LakeHold.git
cd LakeHold
cp .env.example .env
```

### 2. Choose how to run it

**Everything in Docker.** One command brings up the backing services, the identity provider, the
API, and the dev server. The website is served at http://localhost:5399; the API is on `:5200`.

```bash
make dev
```

`make dev` is `docker compose --profile linq up` plus a banner listing the URLs and the seeded
sign-in details you need in step 3, so both query languages are available from the start. Plain
`docker compose up` starts the same stack without the C# LINQ planner, and the language picker then
offers SQL only.

The planner is profile-gated because a production deployment opts into it with a managed secret —
standard private deployments must supply `LAKEHOLD_LINQ_PLANNER_KEY`. Development has a default, so
it costs you nothing here. The public evaluation target, `make demo`, activates the profile and
generates its internal credential automatically. See the
[complete LINQ Workbench guide](https://lakehold.dev/docs/linq-workbench).

**Or — app on the host.** Start only the backing services in Docker, then run the two app processes
yourself. This is the faster inner loop for development.

```bash
docker compose up -d postgres minio minio-bucket jaeger   # backing services
dotnet run --project src/Lakehold.Api                      # API on :5200
npm start --prefix web/lakehold-ui                         # UI on :5399
```

Same URLs either way. The dev server proxies `/api`, `/mcp`, and MCP authorization metadata to
`NG_API_URL`, which falls back to `localhost:5200` when nothing sets it.

### 3. Sign in

**Authentication is required, including locally.** There is no configuration in which it is not, so
the workbench asks who you are before it shows you anything.

The development stack bundles an identity provider (Keycloak) and enables it by default, with a
realm already seeded. `make dev` prints everything below as it starts:

|                   |                                                               |
| ----------------- | ------------------------------------------------------------- |
| Website           | http://localhost:5399                                         |
| API               | http://localhost:5200                                         |
| Identity provider | http://localhost:5401 — Keycloak console is `admin` / `admin` |

Visit http://localhost:5399, click **Open the workbench**, then **Continue with your identity
provider**. Two users are seeded, both with the password `lakehold`:

| Sign in as | You get                                                                                                          |
| ---------- | ---------------------------------------------------------------------------------------------------------------- |
| `analyst`  | Owner of the `demo` workspace — query, write, maintenance, backup, and eject                                     |
| `admin`    | Instance administration — provision workspaces, catalogs, and credentials, and administer users in any workspace |

Either way you land on a seeded catalog: a `demo` workspace with an `analytics` catalog of 250,000
events and 5,000 customers, so there is something to run against before you load data of your own.

**Sign out** ends the identity provider's session as well as LakeHold's, so signing back in starts
from a login form and you can return as somebody else. Switching between the two seeded users needs
nothing special.

**Machines and unattended scripts use API tokens.** Interactive MCP agents may instead sign in as
their operator through OIDC. Paste an API token into the same dialog under **or use an API token**,
or send it as `Authorization: Bearer lkh_…`. Issue tokens under **Users → API tokens**. Full detail —
production first-run, adding users, swapping Keycloak for your own provider, and connecting clients
and agents — is in the
[identity provider setup guide](https://github.com/skuirrels/LakeHold/blob/main/docs/IDENTITY-PROVIDER-SETUP.md).

### Build and test

```bash
dotnet build Lakehold.slnx        # restore + build every project
dotnet test Lakehold.slnx         # integration tests skip unless their service is configured
npm test --prefix web/lakehold-ui
npm run build --prefix web/lakehold-ui
```

---

## The tools you'll use

There are five ways into a LakeHold catalog. LakeHold-hosted surfaces resolve through the same
capability and session boundaries, so you can mix them freely. HTTP may use only an explicitly
configured, catalog-scoped demo reader without a presented credential; MCP is stricter and never
accepts that identity. An application embedding the EF Core provider supplies its own database and
storage connection configuration.

| Tool                  | What it is                                                                                                                                                                                                                                                                                                                                                   | Best for                                                              |
| --------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | --------------------------------------------------------------------- |
| **The workbench**     | A browser query IDE with built-in SQL and an optional isolated C# LINQ planner, catalog-aware completion, generated SQL, diagnostics, history, snapshots, and maintenance. Ships seeded.                                                                                                                                                                     | Exploration, authoring, and operations.                               |
| **A Postgres client** | LakeHold speaks the PostgreSQL wire protocol, so `psql`, DBeaver, or Npgsql connect to a catalog with no driver or plugin. The user is the tenant and the database is the catalog.                                                                                                                                                                           | Existing SQL clients and streamed results.                            |
| **.NET & EF Core**    | Through `DuckDB.EFCoreProvider` your application model and your lake tables are one model.                                                                                                                                                                                                                                                                   | .NET applications on the same schema.                                 |
| **The HTTP API**      | Minimal-API endpoints for queries, schemas, history, snapshots, maintenance, eject, backup/restore, and change-feed subscriptions.                                                                                                                                                                                                                           | Automation and integration.                                           |
| **MCP**               | An authenticated Model Context Protocol server with catalog, time-travel, CDC, physical-inspection, audit-history, saved-query, connector, and snapshot-bound maintenance tools. Writes and operator commands are separately gated. Development enables it by default; an instance operator changes the live controls under System Settings with no restart. | AI agents that need discoverable, capability-scoped lakehouse access. |

### Connect Codex or Claude Code as yourself

An operator first enables MCP and sets the externally reachable **Public base URL** under System
Settings. A fresh development stack already enables MCP and registers the public, PKCE-only
`lakehold-mcp` client. Connect Codex to the Workbench origin:

```bash
codex mcp add lakehold \
  --url http://localhost:5399/mcp \
  --oauth-client-id lakehold-mcp
```

Then start the browser login:

```bash
codex mcp login lakehold
```

Keep the client id so Codex uses the registered public client instead of dynamic registration. Do
not pass `--oauth-resource`: LakeHold's OAuth metadata supplies it, and configuring it twice causes
Keycloak to reject the request as a duplicated parameter.

Claude Code uses the same public client and discovers the resource from LakeHold's OAuth metadata:

```bash
claude mcp add --transport http --client-id lakehold-mcp \
  lakehold http://localhost:5399/mcp
claude mcp login lakehold
```

When the bundled Keycloak login opens, use `analyst` / `lakehold` to reach the seeded `demo`
workspace. The `admin` account administers the instance and deliberately cannot query tenant data.
For a production deployment, replace the URL and client id with those shown in **System Settings**
and registered at the identity provider. If your provider requires scopes, add them to Codex login
as a comma-separated list with `--scopes`.

The authorization server signs you in; LakeHold resolves that identity to your membership row, so
the agent receives your workspace role rather than a shared machine identity. The access token must
target the exact MCP URL advertised by `/.well-known/oauth-protected-resource/mcp`. A deployment may
still use an `lkh_…` API token instead for unattended automation; keep the secret in an environment
variable and send it as the bearer header, never in committed configuration. See the full
[`docs/MCP.md`](https://github.com/skuirrels/LakeHold/blob/main/docs/MCP.md#connecting-a-client) guide
for token configuration, verification, and the complete tool inventory.

---

## Enterprise Data Platform capabilities

LakeHold is building toward a focused Enterprise Data Platform: one private surface to acquire,
store, govern, serve, secure, and operate organisational data. The current product already supplies
the governed lakehouse, identity, audit, consumption, recovery, and exit foundations. The managed
connector work adds the first governed source-to-table path.

The boundaries matter:

- **Available on main today:** open DuckLake/Parquet storage, PostgreSQL control and metadata,
  Workbench and HTTP queries, PostgreSQL wire SQL, EF Core, MCP, scoped identity, audit, time travel,
  CDC, maintenance, backup/restore, and verified eject.
- **Shipped in v1.3.0:** managed REST/gRPC full snapshots, PostgreSQL and
  HubSpot and Kafka Avro incremental adapters, commit-fenced checkpoints, replay-safe upsert, retries/dead letters,
  mappings, schema policy, external secret references, safe egress, telemetry, and retained lineage.
- **Not complete:** a broad production-certified adapter ecosystem, catalog search and
  classification, end-to-end lineage, governed semantic metrics, Power BI compatibility, and live
  open multi-engine access.

Read the [Enterprise Data Platform overview](https://lakehold.dev/enterprise-data-platform), the
[managed connector guide](https://lakehold.dev/docs/connectors), and the
[status-controlled EDP delivery plan](https://lakehold.dev/docs/enterprise-data-platform-roadmap).

---

## The workbench, feature by feature

The browser workbench is the fastest way to explore a catalog and run maintenance. It is laid out top
to bottom: a workspace and catalog picker, a schema explorer on the left, a language-aware editor,
and a tabbed
output pane below it with eight panels — **Results**, **Query history**, **Data history**, **Storage**,
**Changes**, **Backups**, **Eject**, and **Schedule**.

### Workspace and catalog pickers — _top bar_

A workspace is a tenant; a catalog is the tenant-qualified data unit selected for your session. Pick
one of each — every query, history entry, and maintenance run is routed to that pair. The selected
attachment and read-only mode protect catalog routing and catalog writes; they do not sandbox
arbitrary SQL from process-visible files, URLs, secrets, or new attachments.

An instance administrator creates additional tenants under **System Settings → New workspace**.
The workspace slug is its stable identifier in URLs and credentials; its display name is what the
Workbench shows. **New catalog**, directly below it, creates the first or next isolated data unit in
that workspace and selects its storage placement.

### Catalog explorer — _left sidebar → Catalog_

Browses the selected catalog's schemas, tables, and columns. Click an item to insert its name into
the editor, so you can build a query without retyping identifiers.

### Saved queries and published views — _left sidebar → Saved queries_

Save authored SQL or C# LINQ with its language, name, and description. SQL definitions accept one
`SELECT`, query-producing `WITH`, or `VALUES` statement; DuckDB's parser rejects `WITH`-prefixed
inserts, updates, and deletes before they are saved. A saved query belongs to the selected catalog,
so another catalog may reuse its name; it carries an optimistic
revision and always runs through a read-only catalog attachment—even when its author is an owner.
Readers can list, open, and run definitions; editors and owners can create, revise, publish,
unpublish, and delete them.

**Publish** exposes a saved query as a DuckLake view for PostgreSQL-wire, BI, and other SQL clients.
The first publication refuses to overwrite an existing object. Editing the saved SQL does not
silently change that external contract: the library marks the view **republish needed** until an
editor explicitly republishes the new revision. A published definition must be unpublished before
it can be deleted or moved to a different schema/name. Concurrent edits and publication operations
are serialised before view DDL begins, and a failed metadata finalisation reconciles the live target
so the next attempt remains recoverable. A published LINQ definition records its translation schema
fingerprint; a changed catalog is shown as schema drift and requires review and republish. A LINQ
plan with bound parameters can be saved and run but cannot become a view, because DuckDB view DDL
has no parameter-binding lifetime.

```http
POST /api/tenants/{tenant}/catalogs/{catalog}/saved-queries
Content-Type: application/json

{
  "name": "Revenue by country",
  "description": "Stable reporting grain",
  "sql": "SELECT country, sum(revenue) AS revenue FROM events GROUP BY country",
  "language": "sql"
}
```

The returned `revision` is required by update, publish, unpublish, and delete operations. A stale
revision receives `409 Conflict` rather than overwriting another author's edit.

### SQL and C# LINQ editor — _centre pane_

The CodeMirror editor provides syntax highlighting, indentation, search, catalog/table/column
completion, and `⌘⏎` (`Ctrl+Enter`) execution. SQL accepts arbitrary DuckDB/DuckLake statements;
statements LakeHold does not specifically recognise fall back to the ordinary streaming path.

When the optional planner is healthy, choose **C# LINQ** from the language selector. The Workbench
loads a catalog-aware starter and keeps a separate buffer for each language. It accepts one
side-effect-free query expression and shows compiler diagnostics at their source positions. The
result can be an `IQueryable` or `Count`, `LongCount`, `Any`, `Min`, `Max`, `Sum`, or `Average`.
Generated parameterized DuckDB SQL is visible with the result. Removing or losing the planner keeps
saved LINQ source readable but disables execution and authoring until the language returns; it is
never relabelled as SQL. Read the [complete C# LINQ guide](https://lakehold.dev/docs/linq-workbench)
for examples, supported types, deployment, security, API contracts, and troubleshooting.

### File import — _centre pane → Import data_

Choose a browser-local CSV or XLSX file and create a new table without copying the file onto the API
host yourself. For CSV, **Automatic** mode lets DuckDB detect the delimiter, quoting, header, line
endings, and types. If a malformed row appears after detection, LakeHold rolls the strict attempt
back and automatically retries the same request-scoped upload with a full-file scan, reject capture,
and malformed-row skipping. The completed import clearly reports any rejected rows.

**Advanced CSV settings** exposes the parser choices and starts with the production-export profile:

```sql
delim = ';', quote = '"', escape = '', new_line = '\r\n',
header = true, sample_size = -1, ignore_errors = true, store_rejects = true
```

XLSX import uses DuckDB's native Excel reader. Leave **Worksheet** empty to import the first
worksheet, or enter a worksheet name explicitly. LakeHold supports the modern `.xlsx` format; legacy
`.xls` workbooks are not supported.

The upload and import are one streamed request. LakeHold writes the request body directly into
disposable node-local scratch space, creates the table through the selected catalog's DuckDB
session, and removes the scratch file before returning. On Unix hosts the scratch directory and
files are owner-only. Durable rows go to the catalog's configured local or object storage; no later
request depends on the node that accepted the upload. Imports require editor or owner access and
never replace an existing table.

When CSV rejects are enabled—explicitly or through automatic recovery—DuckDB skips malformed rows
and LakeHold returns up to the first 100 parser errors to the dialog. Capture is deliberately capped
at one sentinel beyond that preview, so a hostile or badly malformed file cannot create an unbounded
reject table; the dialog marks the report as truncated when more errors exist. Temporary reject
tables are uniquely named and removed after the response is assembled.

The default per-file and aggregate node scratch ceilings are both 5 GiB, with two concurrent uploads
and a 1 GiB free-space floor. Configure them with `Lakehold:CsvImport:MaxBytes`,
`MaxAggregateScratchBytes`, `MaxConcurrentUploads`, `MinimumFreeBytes`, and `ScratchRoot`. LakeHold
also scavenges scratch files older than `StaleFileAge` during coordinator startup. Reverse proxies
must allow the same per-request size.

### Results grid — _Output → Results_

Streams the result of the last statement into a grid, with the row count and elapsed time shown in
the toolbar. Integers and decimals beyond JavaScript's safe range are transported losslessly as
strings so nothing is silently rounded.

> Results are capped by `LakehouseOptions.MaxRowsPerResult` so a large query cannot exhaust memory.
> When the cap is hit you see a "Row limit reached" tag. The Postgres wire endpoint streams rows to
> the socket and does not apply the cap.

### Query history — _Output → Query history_

Every statement the workspace has run, successful or failed, with its row count, duration, and
timestamp. Click a row to replay it into the editor. Postgres-wire traffic shows up here too, so
client queries are visible for the first time.

### Data history and time travel — _Output → Data history_

A table-oriented history browser over the catalog's DuckLake snapshots. The timeline shows commit
time, commit message, schema version, and schema transitions. Choose a table, then:

- **Browse** reads its rows inline exactly as they stood at that snapshot.
- **Changes** drills into only the row-level inserts, deletes, and update pre/post-images committed
  in that snapshot.
- **Compare changes** compares two table states. Because the change feed is inclusive, the browser
  reads after the baseline through the target rather than counting the baseline commit again.
- **Restore…** requests a read-only server plan with live and historical row counts plus schema
  differences. Only **Confirm atomic restore** applies it.

Historical previews display at most 500 rows and fetch one additional sentinel row, so the UI marks a
prefix as truncated instead of presenting it as complete. They can be opened in the SQL editor for
further filtering. Read-only users can browse and compare but are not offered restore. Increase the
Timeline limit when the desired snapshot is older than the newest 100. See
[Time travel](#time-travel) below for the underlying SQL.

### Storage — _Output → Storage_

What each table physically weighs: live rows, total size, Parquet file count, average file size,
delete-file overhead — and, in the last column, whether the maintenance buttons above are worth
pressing.

Two advisories appear there:

- **Flush pending** — the table holds rows that are committed but not yet written to Parquet. This is
  also the only thing separating a table whose data is entirely inlined from an empty one: both report
  zero files, so without the row count a table with data in it would read as empty.
- **Fragmented** — the table has drifted into the small-file problem: more than one file, averaging
  below the catalog's `target_file_size` (or the deployment's advisory floor when it has never set
  one). A _single_ small file is never flagged, because a lone file cannot be merged with anything.

Select a table — from this rollup or with **Inspect** in the catalog tree — to open one inspector:

- **Overview** combines the logical schema with the storage figures and the current DuckLake
  partition keys. If partitioning evolved, its earlier snapshot-bounded specifications remain
  available as history.
- **Files** shows real paths and byte counts, with each data file paired to the delete file that
  applies to it.
- **Columns** computes a live profile only when opened: exact null counts, min/max, approximate
  distinct counts and quartiles, followed by an on-demand bounded distribution for one selected
  column.

Files and Columns share an **as of** selector, so both the physical layout and the values can be read
at an earlier snapshot. Views remain inspectable and profileable, but correctly show no files or
partition layout of their own.

> Every figure comes from DuckLake's own catalog, never from listing the data path. A directory
> listing would show orphaned files and live data identically, and enumerating an object store is an
> unbounded `LIST` that times out at scale. A snapshot predating the table's creation is reported as
> an error rather than an empty list — an empty list would be a different and false statement.

### Changes — _Output → Changes_

The row-level change feed and the webhook subscriptions that push it, in one place: a subscription's
last delivered snapshot only means something next to the snapshots the feed is showing.

Pick a table and a starting snapshot to read what changed. An update arrives as a **pre-image and
post-image pair** sharing a row id, styled differently from a delete, so you can take either the net
effect or the diff. Below the feed, create a subscription with an endpoint URL and a signing secret,
or delete one — deletion asks first. Consecutive failures and the last error are shown per
subscription, because a subscription you cannot observe is one you cannot trust.

### Backups — _Output → Backups_

Every backup generation, newest first, with its snapshot and table count. A complete generation offers
**Restore…**; an incomplete one is marked and offers nothing, because a generation with no manifest
died partway through and restoring it could silently reinstate deleted rows.

Restore always writes a **new** catalog file and never overwrites, so the catalog you are rescuing is
untouched whatever happens. A bare file name lands beside the server's other catalogs; an absolute
path is used as given. The result reports the absolute path it actually wrote.

### Eject — _Output → Eject_

The bundles written by the exit path, newest first. Expand one to see its per-table attestation: row
counts and SHA-256 digests, and whether the bundle is signed. A bundle with no manifest is marked
untrusted rather than listed as if it were usable.

Eject has no dry run — it only ever writes a new bundle and never touches the catalog — and an
**include history** option decides whether the metadata catalog travels with the data so time travel
survives the export.

### Schedule — _Output → Schedule_

The scheduled maintenance run log: which job ran against which workspace and catalog, when, how long
it took, and whether it succeeded. Scoped to what your credential is allowed to see.

### Maintenance operations — _top bar → Maintenance_

Five one-click operations:

| Operation   | What it does                                                   | Destructive?                 |
| ----------- | -------------------------------------------------------------- | ---------------------------- |
| **Flush**   | Writes inlined commits out as Parquet.                         | No                           |
| **Compact** | Merges small Parquet files.                                    | No                           |
| **Backup**  | Exports the metadata catalog to Parquet under the backup root. | No                           |
| **Expire**  | Drops snapshots older than seven days.                         | **Yes — dry-run by default** |
| **Cleanup** | Deletes data files no longer referenced by any snapshot.       | **Yes — dry-run by default** |

The two destructive operations return what they _would_ do and change nothing until you click
**Apply for real**. Flush and Compact are the only operations that commit — they run inside a
transaction labelled `lakehold maintenance: …` so platform writes stay distinguishable from a
tenant's own.

The first three also run on a schedule, so you rarely need this menu — see
[Automatic maintenance](#automatic-maintenance).

---

## Automatic maintenance

Flush, backup, and compact run on a cron schedule against **every writable catalog in every
workspace**, so a lakehouse stays healthy with nobody clicking anything. Read-only shares are skipped:
they have nothing to maintain.

| Job         | Runs by default                     | Why that cadence                                                                                                                                                                                                   |
| ----------- | ----------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **Flush**   | Every 15 minutes — `0 0/15 * * * ?` | The consequential one. Inlined commits that have never been flushed are the one category of data that is _permanently_ unrecoverable if the catalog is lost, so this interval is the upper bound on that exposure. |
| **Backup**  | Hourly, on the hour — `0 0 * * * ?` | Bounds how much snapshot history a catalog loss costs.                                                                                                                                                             |
| **Compact** | 02:30 daily — `0 30 2 * * ?`        | Non-destructive but I/O heavy, so it stays off-peak.                                                                                                                                                               |

**Expire and cleanup are never scheduled.** They delete data, and automating an irreversible deletion
would quietly undo the safety the rest of the product argues for. They stay manual and
dry-run-by-default, whatever else you configure.

### Changing the schedule

Everything lives under `Lakehold:Maintenance` in `appsettings.json`:

```jsonc
{
  "Lakehold": {
    "Maintenance": {
      "Enabled": true,
      "FlushCron": "0 0/15 * * * ?",
      "BackupCron": "0 0 * * * ?",
      "CompactCron": "0 30 2 * * ?",
      "NodeId": "", // defaults to the machine name
      "LeaseDuration": "00:30:00",
    },
  },
}
```

| Setting                                    | Default      | Meaning                                                                  |
| ------------------------------------------ | ------------ | ------------------------------------------------------------------------ |
| `Enabled`                                  | `true`       | Whether the scheduler runs at all. `false` makes every operation manual. |
| `FlushCron` · `BackupCron` · `CompactCron` | above        | When each job fires.                                                     |
| `NodeId`                                   | machine name | Identifies this node when claiming a maintenance lease.                  |
| `LeaseDuration`                            | 30 minutes   | How long a lease stays valid if the node holding it never releases it.   |

Any of them can be set as an environment variable instead, which is usually how a container is
configured — double underscores separate the levels:

```bash
Lakehold__Maintenance__FlushCron="0 0/5 * * * ?"
Lakehold__Maintenance__Enabled=false
```

> **These are Quartz cron expressions, not Unix cron.** The first field is **seconds**, so there are
> six, and `0 0/15 * * * ?` means _every 15 minutes_, not _every 15 hours_. Day-of-month and
> day-of-week cannot both be constrained, which is why one of them is `?`. Paste a five-field Unix
> expression such as `*/15 * * * *` and the API will not start: it fails with
> `System.FormatException: Unexpected end of expression`. Times are the server's local time zone, so
> the 02:30 compaction follows the host's clock rather than UTC.

A missed window is skipped rather than replayed: if the host was asleep or busy past the trigger
time, the job waits for the next one instead of firing a backlog the moment it wakes.

### Seeing what actually ran

A schedule you cannot observe is a schedule you will not rely on, so recent runs are readable:

```bash
curl -s http://localhost:5200/api/maintenance/schedule
```

Each entry gives the job, workspace, catalog, start time, elapsed milliseconds, whether it succeeded,
and a detail line. It is an in-memory ring of the last 100 runs — an operational read-out, not an
audit trail; query history covers durable auditing, and the list starts empty after a restart. The
effective schedule is also logged once at start-up.

The list is scoped to your credential, the same way the tenant listing is: an instance token sees
every workspace's runs, a tenant token sees only its own, and a catalog-narrowed token sees only that
catalog's.

A catalog skipped because another node was doing the work is recorded as exactly that, rather than
being left out: "nothing ran" and "another node ran it" have to be tellable apart when you are
working out why a backup is missing.

### Running more than one node

Schedules are held in memory on each node, so every node fires the same sweep at the same moment.
Duplicate _work_ is excluded by a lease taken per job, per catalog — not by centralising the
_schedule_, which is identical everywhere because it comes from configuration.

The lease only engages where a catalog can genuinely be shared, which is where its metadata is in
PostgreSQL; a local file catalog cannot be opened twice at once anyway. Two things are worth setting
deliberately: `NodeId`, if several containers share a host and would otherwise present the same
identity, and `LeaseDuration`, which only needs to exceed the longest plausible single run so a slow
compaction is never mistaken for a dead node.

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

Open **Data history** in the workbench. It lists snapshots newest first and lets you browse a selected
table at that version, inspect one commit, or compare two versions without first writing SQL. The
same timeline is available over the API at `GET …/snapshots`, and in SQL through the catalog's
`snapshots()` function:

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

### Restoring table data safely

Time travel _reads_ the past; **Restore…** in Data history returns a read-only plan before changing
anything. It shows the live and historical row counts, the shared columns that will be restored,
current-only columns that will receive current defaults or nullability, and historical-only columns
that will be ignored. **Confirm atomic restore** is then a separate action.

Apply stages the historical rows before deleting live rows and inserts them through the existing table
definition inside one labelled transaction. The current defaults, nullability, and constraints remain
authoritative; if any historical row is incompatible, the transaction rolls back completely. The
successful restore records a new snapshot, so earlier row states remain available. Confirmation also
carries the live snapshot id from the reviewed plan: if another commit lands in between, apply refuses
and requires a fresh review.

The same unversioned API used by the workbench is available to an editor or owner:

```http
POST /api/tenants/{tenant}/catalogs/{catalog}/snapshots/42/restore-table
Content-Type: application/json

{ "schema": "main", "table": "events", "apply": false }
```

Send the same request with `"apply": true` and the returned `"currentSnapshotId"` as
`"expectedCurrentSnapshotId"` only after reviewing the plan. A snapshot covers the whole catalog, but
this operation restores one base table at a time and refuses a snapshot from before the table existed.

> Do not substitute `CREATE OR REPLACE TABLE … AS SELECT … AT (…)`: CTAS discards the current table's
> defaults and nullability. `DELETE` followed directly by `INSERT … AT (…)` is also unsafe because the
> historical read can resolve through the pending delete. The restore operation exists to stage first
> and guarantee rollback.

### What it's good for

| Goal                            | How                                                                                                                 |
| ------------------------------- | ------------------------------------------------------------------------------------------------------------------- |
| Recover rows deleted by mistake | Read the old version and write the rows back: `INSERT INTO events SELECT * FROM events AT (VERSION => 41) WHERE …`. |
| Audit how a value changed       | Query the same key across several snapshot ids.                                                                     |
| Reproduce a past report         | Run the report's query with `AT (TIMESTAMP => …)` set to the report date.                                           |
| Diff two versions               | `EXCEPT` or `INTERSECT` between two `AT (…)` reads.                                                                 |

> **History is not kept forever.** Snapshots accumulate until you expire them. **Expire** (Maintenance)
> drops snapshots older than seven days and **Cleanup** deletes the files they alone referenced — both
> are irreversible and dry-run by default, so time travel to an old snapshot keeps working until you
> explicitly apply them. To carry history across an export, back up or eject _with the metadata
> catalog_; the data files alone recover the latest state only. See
> [Catalog backup and restore](#catalog-backup-and-restore).

---

## Data operations in depth

These are the features that make LakeHold a lakehouse rather than a query box. Each has a panel in
the workbench, described above; this section is what those panels are doing and the routes to drive
them from your own code. They all sit under `/api/tenants/{tenant}/catalogs/{catalog}/`.

### Storage and table detail

`GET …/storage` · `GET …/storage/files?schema=&table=&snapshot=&limit=`<br>
`GET …/table-detail?schema=&table=` · `GET …/table-profile?schema=&table=&snapshot=`<br>
`GET …/column-distribution?schema=&table=&column=&snapshot=&limit=`

The figures behind the Storage panel, from DuckLake's own metadata rather than a directory listing.
The rollup carries per-table row counts, inlined rows, file counts and sizes, delete-file overhead,
and the two advisories; the response also reports the threshold each compaction verdict was computed
against, so a caller can check the reasoning rather than trust it. Schema and table are query
parameters, not path segments, because a table name may contain a dot or a slash.

All are reads and need only `TenantData` — knowing how large a table is or what values an authorised
reader can query is not the owner's decision to authorise, even though pressing Compact is.
Encryption-key columns are never projected into a response.

The detail response combines the schema, one table's existing storage projection, and current plus
historical partition specifications under one Duckling gate. The profile reads the table's **logical
rows**, not `ducklake_file_column_stats`: physical stats can omit inlined rows and retain values later
removed by merge-on-read deletes. Distinct counts and quartiles are labelled approximate. A
distribution is requested separately and capped at 50 buckets; no raw sample rows are returned.

> **Caveat.** The file list returns one row per file and is capped, reporting `truncated` when it
> stops short. A snapshot predating the table's creation is a `400` carrying the engine's own
> message, not an empty list.

### Eject — the exit path in one call

`POST …/eject` · `GET …/ejects`

Re-materialises every table through the catalog into ordinary Parquet, plus the metadata catalog when
you want history. Because it re-reads rather than copying files, merge-on-read deletes are applied,
superseded update rows are gone, inlined commits are included, and DuckLake's internal columns never
leak. Every table is counted back through a plain Parquet reader and compared to the catalog before
the manifest is written; the manifest carries per-table row counts, SHA-256 digests, and an HMAC
signature when a key is configured.

> **Caveat.** A copy of the data path is _not_ an eject — copying files resurrects deleted rows and
> duplicates updated ones. Eject is read-only and works on a read-only share, so it needs no flush
> first. See [`docs/EXIT-PATH.md`](https://github.com/skuirrels/LakeHold/blob/main/docs/EXIT-PATH.md)
> for the procedure eject automates.

### Catalog backup and restore

`GET …/backups` · `POST …/backups/restore`

The metadata catalog is exported to Parquet on a schedule and can rebuild a working catalog from that
export — row counts, deletions, updated values, views, and `AT (VERSION => n)` time travel all
intact. A catalog whose metadata lives in PostgreSQL restores into a plain DuckDB file, so it is an
exit path from the catalog database, not just a copy.

The engine supports an `s3://` backup root, but the packaged API currently resolves the backup root
beneath `Lakehouse:StateRoot`; in the production Compose stack that is the same state volume as the
catalog. A catalog backup is therefore not protection from losing the whole volume. Export a
consistent state archive off-host and follow the
[disaster-recovery runbook](https://lakehold.dev/docs/disaster-recovery).

A relative target path is resolved against the server's metadata root, so a bare file name lands
beside the catalogs it belongs with; an absolute path is used as given, and the response reports the
absolute path that was actually written.

> **Caveat.** Restore never overwrites an existing catalog, and refuses an export with no completion
> manifest — a partial export missing `ducklake_delete_file` would silently reinstate deleted rows.

### Change data capture

`GET …/cdc/snapshots/{snapshot}/changes` · `GET …/changes` · `…/subscriptions` ·
`…/cdc/consumers`

DuckLake already records what each snapshot changed, so LakeHold exposes it directly: a typed pull
API for opaque cursor-paged change pages, and outbound webhooks fired per new snapshot and signed
with HMAC-SHA256. Updates arrive as a paired pre-image and post-image sharing a row id, so you can
take net effect or diff them. No Debezium, no Kafka, no second pipeline.

> **Caveat.** Delivery is at-least-once. PostgreSQL keeps one stable delivery id/body and expiring
> worker lease per subscription and snapshot; the cursor advances only after a 2xx, so a crash can
> replay but cannot skip. Each attempt has a fresh timestamp and signature over the
> `v1.<timestamp>.<delivery-id>.<body>` base. Deduplicate by delivery id, and use the pull cursor when
> an inline payload is truncated. Live subscriptions, including paused subscriptions, and active
> registered consumers block snapshot expiry from removing changes they still need.

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

LakeHold is built on `DuckDB.EFCoreProvider`, so your application model and your lakehouse tables can
be the same EF Core model — no transformation layer, no schema drift. The split is by whether the
workload has a known shape:

- **Modelled state** — tenants, catalogs, saved queries, and audit history — uses
  `ControlPlaneContext` on native DuckDB, which needs sequences and `RETURNING`.
- **Arbitrary result shapes** — anything you query against a lake catalog — stream through the
  provider's dynamic path, capped and cancellable, without inventing fake entity types.

The repository now includes `Lakehold.Client` and a first-party DuckDB replication worker. The
initial worker is intentionally narrow: configured keyed or append-only tables, scalar types, and
fail-closed handling for schema drift or snapshot gaps. It is an executable deployment component,
not yet a published general-purpose NuGet change-stream package.

---

## Authentication

LakeHold accepts **API tokens** for machines and **OIDC identities** for people and interactive MCP
agents. An API token names one tenant, may be narrowed to a single catalog, carries a role (`owner`,
`editor`, `reader`), and can be revoked — which closes the HTTP API, MCP, and PostgreSQL wire endpoint
together. Users sign in through your identity provider — see the [identity provider setup
guide](https://github.com/skuirrels/LakeHold/blob/main/docs/IDENTITY-PROVIDER-SETUP.md).

**A credential is required, in every configuration.** There is no switch that turns this off — the
one that existed defaulted to off, which meant the authorization layer did nothing in the setup most
people actually ran. If you want to publish a catalog to visitors who have no credential, name it:

```json
{ "Lakehold": { "Auth": { "DemoTenant": "demo", "DemoCatalog": "analytics" } } }
```

That is a read-only identity scoped to exactly that catalog, and it fails closed if either value is
missing.

### Getting your first token

A node with no tokens mints one on start-up, logs it **once**, and never again. It is instance-scoped:
it provisions tenants, catalogs, and other tokens, but deliberately cannot read data — so a leaked
admin credential is a visible provisioning problem rather than a silent data breach.

**The workbench does this for you.** Open a fresh deployment and it asks for that bootstrap token,
then for a workspace and catalog name, creates both, and mints the token that can query them —
showing it once, because the server keeps only a hash. Adopting it is one click. Additional
workspaces and catalogs are created under **System Settings** with an instance credential. The steps
below are the same thing by hand, for a script or a deployment with no browser in reach.

```bash
docker compose -f compose.production.yaml up -d
docker compose -f compose.production.yaml logs api | grep -i bootstrap

API=http://localhost:8080/api
ADMIN='lkh_admin_…'

curl -X POST $API/tenants -H "Authorization: Bearer $ADMIN" \
  -H 'Content-Type: application/json' -d '{"slug":"acme","displayName":"Acme"}'

curl -X POST $API/tenants/acme/catalogs -H "Authorization: Bearer $ADMIN" \
  -H 'Content-Type: application/json' -d '{"name":"analytics"}'

curl -X POST $API/tenants/acme/tokens -H "Authorization: Bearer $ADMIN" \
  -H 'Content-Type: application/json' \
  -d '{"name":"bi","role":"reader","catalogName":"analytics"}'
```

**Mind the port.** The production stack does not publish the API — nginx serves only the private
Workbench at `:8080/workbench` and proxies `/api` on the same origin, so provisioning goes there.
The public website routes and isolated C# LINQ planner are enabled by default with `make demo`; its
internal planner credential is generated automatically. On the development stack the API is
published directly and the base URL is `http://localhost:5200/api` instead. Every `$API` in this
section is one or the other.

Set `Lakehold__BootstrapToken` if your platform injects credentials and cannot scrape a log.

A token is shown once at creation and stored only as a SHA-256 hash, so it cannot be recovered — from
the API or the database. `admin` is a reserved tenant slug: a tenant of that name would mint tokens
indistinguishable from instance-scoped ones.

### Roles

| Role     | Query | Write                              | Maintenance, restore, eject | Manage tokens |
| -------- | ----- | ---------------------------------- | --------------------------- | ------------- |
| `reader` | yes   | no — catalog is attached read-only | no                          | no            |
| `editor` | yes   | yes                                | no                          | no            |
| `owner`  | yes   | yes                                | yes                         | yes           |

A token created without `role` is a **reader**, and so is one naming a role that is not recognised —
so the cost of a typo is a credential that can read rather than one that can do everything. Pass
`"role":"owner"` or `"role":"editor"` deliberately when you mean them, and confirm what you issued
with `GET /api/tenants/{tenant}/tokens`, which shows the role and never the secret.

### Capability comes from attachment

A `reader` token does not get a verb check that clever SQL might route around — its selected catalog
is attached **read-only**, so a write through that catalog handle fails in the engine itself.
Tenant-qualified routing and storage keep same-named catalogs distinct, but the attachment is not a
general sandbox for process-visible files, URLs, secrets, extensions, or new attachments. Shared
untrusted SQL remains behind the documented worker-containment gate.

### Revoking

```bash
curl -X DELETE $API/tenants/acme/tokens/2 -H "Authorization: Bearer $ADMIN"
```

Revocation is immediate and closes **both** surfaces: the HTTP API and the PostgreSQL wire endpoint
resolve against the same store, so a revoked token stops a BI tool as well as a script. There is no
un-revoke — issue a new token.

### Signing in to the workbench

Where OIDC is configured, **Continue with your identity provider** starts an authorization-code +
PKCE login. The API returns an HttpOnly session cookie; provider tokens never enter browser
JavaScript. A person carrying the configured system-admin claim changes MCP controls under System
Settings; they, and the owner of a workspace, admit users and issue, review, or revoke that
workspace's client credentials under Users.

The token field remains the machine and break-glass path. A pasted token is held for that browser
session only and sent as a bearer token on API calls. With neither OIDC nor a token, only the
explicitly configured demo tenant/catalog can be read anonymously; all other access is refused.

When a deployment requires authentication and the browser has no usable token, the workbench says so
and asks for one instead of showing empty pickers — the same panel that runs the first-workspace flow
above. A token that has been revoked, or one from a rebuilt node, lands in the same place: the API
answers 401, and the panel is the remedy rather than an error.

### Connecting a BI tool with a token

Set `Lakehold:PgWire:AllowTokenAuthentication` and give the token as the password, with the tenant as
the user and the catalog as the database. Because a hashed store cannot answer PostgreSQL's MD5
challenge, this uses the cleartext exchange — so the API **refuses to start** unless TLS is required
or cleartext is explicitly acknowledged, rather than quietly putting a credential on the wire.

### When a request is refused

| Code    | Meaning                                                                                                                                                     |
| ------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **401** | No credential when one is required, or one that did not resolve. Malformed, unknown, revoked, and expired are reported identically — you never learn which. |
| **404** | Valid credential, but it does not name that tenant or catalog. Deliberately not 403: a 403 would confirm the tenant exists.                                 |
| **403** | Right subject, wrong capability — a reader running maintenance, or a tenant token calling provisioning.                                                     |

### Signing in as a human

Humans can use **OIDC** (Keycloak, Entra, Authentik, Auth0). Configure an authority and client id,
register `/auth/callback`, and map either tenant/role claims or the system-admin claim. Leave it unset
and the whole path stays off, so an air-gapped install never takes an identity-provider dependency.
Tokens and OIDC resolve to the same principal and capability policy.

The full design, the endpoint and capability tables, and the runbook for closing the door on a fresh
deployment are in
[`docs/AUTHENTICATION.md`](https://github.com/skuirrels/LakeHold/blob/main/docs/AUTHENTICATION.md).

---

## Reference

The design docs go deeper than this guide.

| Document                                                                                                 | Covers                                                                                                         |
| -------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------- |
| [Operations](https://lakehold.dev/docs/operations)                                                       | Production ownership, entry gates, routine work, and the incident, disaster-recovery, and monitoring runbooks. |
| [`docs/ARCHITECTURE.md`](https://github.com/skuirrels/LakeHold/blob/main/docs/ARCHITECTURE.md)           | Architectural rationale and current product boundaries.                                                        |
| [`docs/EXIT-PATH.md`](https://github.com/skuirrels/LakeHold/blob/main/docs/EXIT-PATH.md)                 | The verified open-format exit procedure that eject automates.                                                  |
| [`docs/POSTGRES-WIRE.md`](https://github.com/skuirrels/LakeHold/blob/main/docs/POSTGRES-WIRE.md)         | The wire protocol surface and what is deliberately unimplemented.                                              |
| [`docs/AUTHENTICATION.md`](https://github.com/skuirrels/LakeHold/blob/main/docs/AUTHENTICATION.md)       | The phased plan for API authentication and tenant identity.                                                    |
| [C# LINQ in the Workbench](https://lakehold.dev/docs/linq-workbench)                                     | Enablement, editor model, API contract, isolation, supported types, and troubleshooting.                       |
| [`docs/UI.md`](https://github.com/skuirrels/LakeHold/blob/main/docs/UI.md)                               | The web surfaces around the query Workbench, and why a raw object browser is not one of them.                  |
| [`docs/MCP.md`](https://github.com/skuirrels/LakeHold/blob/main/docs/MCP.md)                             | The MCP server, the tools an agent gets, and the ones deliberately withheld.                                   |
| [`docs/PROVIDER-FEEDBACK.md`](https://github.com/skuirrels/LakeHold/blob/main/docs/PROVIDER-FEEDBACK.md) | Provider capabilities and why the data plane uses its dynamic API.                                             |
| [`README.md`](https://github.com/skuirrels/LakeHold/blob/main/README.md)                                 | Build, run, and the full set of environment variables and test commands.                                       |
