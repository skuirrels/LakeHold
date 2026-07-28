# The workbench and what sits beside it

The plan for the web surfaces LakeHold needs beyond its SQL IDE — principally the **physical layer**:
how much a table weighs, how many Parquet files it is spread across, how much of it is delete-file
overhead, and therefore whether the maintenance buttons already in the toolbar are worth pressing.

Like [`MCP.md`](MCP.md) and [`PUBLIC-API.md`](PUBLIC-API.md), this is a specification and a running
record, written to be worked one step at a time. Nothing here contradicts an invariant in `AGENT.md`;
where a rule already exists this document says how the UI preserves it rather than restating why.

**Status: Phases 1–7 have landed.** `StorageBrowser` reads the footprint, five dedicated routes serve
the storage and inspection surfaces, and
the workbench has eight panels: Results, Query history, Data history, Storage (with a unified
per-table inspector), Changes, Backups, Eject, and Schedule. The left rail switches between the
catalog explorer and the catalog-scoped saved-query library.

Everything the original specification left open has since been **measured against DuckLake on DuckDB
1.5.5** rather than reasoned about. Two of those measurements changed the design, and are called out
below where they land: `ducklake_table_info` does not see inlined data, and the metadata catalog is
hidden from *enumeration* but not from a targeted read.

## What a lakehouse UI conventionally is

Surveyed across Databricks Catalog Explorer, MotherDuck, the DuckDB local UI, Dremio, and the Iceberg
maintenance tooling grown up around Polaris and Nessie. The surfaces are strikingly consistent:

| Surface | The question it answers | Who ships it | LakeHold today |
|---|---|---|---|
| SQL IDE | "Run this" | Everyone | ✅ workbench |
| Catalog tree | "What tables, what columns" | Everyone | ✅ `catalog-explorer` |
| **Table detail** | "How big, how many files, partitioned how" | Databricks, Dremio, Snowflake | ✅ inspector |
| Column profile | "What is *in* the column — nulls, distribution, min/max" | MotherDuck Column Explorer, DuckDB local UI | ✅ live profile |
| History / time travel | "What changed, when; read it as of then" | Databricks History, Iceberg snapshot views | ✅ unified data-history drill-down |
| **Storage & maintenance health** | "Is this fragmented, should I compact, what will cleanup delete" | The Iceberg maintenance category | ✅ readout + advisories |
| Governance — lineage, grants, usage | "Who reads this, who may" | Unity Catalog and enterprise peers | ❌ deliberately |

Two findings from that survey carry the rest of this document.

**The physical layer is a first-class surface everywhere except the DuckDB-family tools.** Databricks'
table-details page leads with file count, data size, and partition columns; the whole Iceberg
maintenance-tooling category exists to render file-size distribution and classify tables
healthy/warning/critical from it. The DuckDB local UI and MotherDuck skip it entirely — correctly, for
what they are. They are single-node analysis tools where nobody *operates* the storage. LakeHold is the
one in this family where somebody does.

**Nobody converged on a file browser.** Databricks has one only for Volumes, which are explicitly
unmanaged files; lakeFS has an object browser because it *is* an object-store versioning layer. For
managed tables every mature UI presents files *through the catalog* — per table, per snapshot — never
as a directory tree. See [Why not a file browser](#why-not-a-file-browser); the reasons are ours, not
borrowed.

## The argument: the maintenance buttons are blind

This is the case for building, and it is stronger than feature parity.

The toolbar in [`workbench.component.html`](../web/lakehold-ui/src/app/workbench.component.html) offers
Flush, Compact, Backup, Expire, and Cleanup. A user presses **Compact** — "merge small Parquet files" —
with no way to know whether the table is three files or three thousand. They press **Flush** without
knowing whether anything is inlined. They press **Cleanup** and judge a destructive operation by a
dry-run text blob.

LakeHold's stated trade is managed elasticity *for infrastructure control*. An operator cannot exercise
control over a layer they cannot see, and explicit dry-run-by-default maintenance (invariant 10) is only
as good as the evidence available for the decision. The storage view is what makes the surface already
built **decidable**.

It also lands on demand the research already recorded. [`COMPETITIVE-RESEARCH.md`](COMPETITIVE-RESEARCH.md)
finds physical layout control the loudest unmet need in DuckLake — partitioning (#301) the top issue by
a factor of 2.4, `target_file_size` (#224) the same complaint from the other side — and its "operational
cluster" is, item for item, self-hosters getting hurt by maintenance they cannot observe. That document
is dated July 2026; cite it with its date.

## Why not a file browser

The obvious reading of "show me the Parquet" is a directory tree over the data path. It is the wrong
build, for four reasons that are all already written down:

1. **Object stores have no directories to enumerate.** The integration tests exist precisely because
   this difference does not show up at compile time. A tree control implies a hierarchy S3 does not have.
2. **A path listing is not the table.** Everything under the data path that the catalog does not
   reference is orphan-cleanup fodder (invariant 11), and superseded update rows and merge-on-read
   deletes mean the files on disk are not the rows in the table (invariant 15). A browser would present
   garbage and live data identically — the same confusion `EXIT-PATH.md` documents the naive glob as
   causing.
3. **Enumerating the bucket is the operation that already fails upstream.** `delete_orphaned_files`
   performing an unbounded full-bucket `LIST` and timing out at scale is
   [ducklake#1090](https://github.com/duckdb/ducklake/issues/1090). A UI that does the same thing on
   every page load reproduces a known production failure for cosmetics.
4. **The credentials are not ours to hold.** Object-store credentials live in provider connection
   configuration and never reach a catalog record, an options object, a response, or a log
   (invariant 8). A browser that lists a bucket wants exactly the thing invariant 8 forbids moving.

**The catalog is the truth, and files are presented through it.** Every figure below comes from
DuckLake's own metadata, which is bounded, transactional, and correct about which files are live.

## What DuckLake already gives us

Before this work the engine touched almost none of it — only `ducklake_snapshots`,
`ducklake_table_changes`, and the metadata tables backup and eject copy.

| Source | Gives | Reachable how |
|---|---|---|
| `ducklake_table_info(catalog)` | Per table: `file_count`, `file_size_bytes`, `delete_file_count`, `delete_file_size_bytes`. **No row count** | **Table function** — ordinary Duckling session |
| `ducklake_list_files(catalog, table, schema =>, snapshot_version =>)` | Per file: `data_file`, `data_file_size_bytes`, footer size, and the paired `delete_file` and its size | **Table function** — ordinary Duckling session |
| `ducklake_snapshots(catalog)` | Snapshot id, time, schema version, change summary | Table function; already used |
| `ducklake_table_stats`, `ducklake_data_file`, `ducklake_delete_file`, `ducklake_schema` | Row counts and schema names, which no table function carries | **Metadata table** — by name, from the session |
| `ducklake_file_column_stats` | Per file per column: `column_size_bytes`, `value_count`, `null_count`, min/max | **Metadata table** — by name, from the session |
| `SUMMARIZE SELECT …` | Live logical column profile: min/max, approximate distinct count, mean/stddev and quartiles | Ordinary Duckling session over the table, optionally at a snapshot |

**Table functions run on the session.** They need no attach, no second connection, and no special
casing — a Duckling can call them exactly as it runs user SQL, and they therefore inherit the tenant
isolation of invariant 4 for free.

**So do targeted reads of the metadata tables — and this corrects the specification.** The original
draft asserted that anything sourced from a `ducklake_*` metadata table has to pay
`MetadataExporter`'s independent-connection machinery. Measured on 1.5.5, that is too strong. The
metadata catalog is indeed invisible to `duckdb_tables()` and `duckdb_databases()` — both return zero
rows for it, exactly as invariant 12 says — but
`SELECT … FROM __ducklake_metadata_<catalog>.ducklake_table_stats` **works from the ordinary session**.
What DuckDB 1.5.4 removed was *enumeration*, not *access*.

That distinction is the whole reason `MetadataExporter` needs its own connection and `StorageBrowser`
does not: the exporter must discover the table list at run time (invariant 12), while a rollup already
knows the table names it wants. `StorageBrowser` therefore reuses
`MetadataExporter.ResolveMetadataAliasAsync` — one place knows how DuckLake attaches its own metadata —
and reads through the session. It also means column profiling is **not** gated on that cost, so its
deferral below rests on product priority alone.

Three traps, all verified:

- `ducklake_list_files` returns `data_file_encryption_key` and `delete_file_encryption_key` as `BLOB`s,
  populated when the database uses encryption. **These are secrets.** They must never be projected
  into a DTO, a response, or a log — invariant 8's rule, arriving through a column rather than a
  connection string. `StorageBrowser` names its columns explicitly and never `SELECT *`, and a test
  asserts on the record's *shape* so an encrypted catalog cannot regress it silently.
- Its `schema` argument is **not optional in practice**. Omitting it raises
  `Table with name t2 does not exist` for anything outside the search path rather than searching, so
  every call passes it. Its `snapshot_version` must be a literal — a table function cannot contain a
  subquery.
- One row per file, so this is a materialising path: the cap of invariant 6 applies. It reports
  truncation the way the change feed does rather than adopting the cursor pagination
  `PUBLIC-API.md` plans, so all these surfaces convert together when that phase lands.

## The surfaces

### Storage tab

A fourth tab beside Results, Query history, and Data history — one row per table:

| Table | Rows | Size | Files | Avg file | Deletes | Maintenance |
|---|---|---|---|---|---|---|
| `main.sessions` | 20,000 | 53 kB | 5 | 11 kB | — | ⚠️ Fragmented |
| `main.staging` | 7 | 0 B | 0 | — | — | ℹ️ Flush pending |

The last column is the point, and it carries **two** advisories rather than the one the specification
anticipated.

**Fragmented** — more than one file, averaging below the target — turns Compact from a guess into an
answer. It requires more than one file deliberately: a single small file cannot be merged with
anything, so flagging it would be advice the operator cannot act on, and training someone to ignore
the column is worse than leaving it blank.

**Flush pending** exists because of a measurement that contradicts the original design. The draft said
this tab came from "a single `ducklake_table_info` call". It cannot:

- `ducklake_table_info` carries **no row count at all**, so rows come from the metadata catalog.
- It does **not see inlined data**. A table holding only inlined rows reports zero files and zero
  bytes — verified engine behaviour 1 seen from the storage side, and *indistinguishable from an empty
  table* on the file figures alone. `main.staging` above is exactly that case: 7 rows, 0 B, 0 files.

Reporting a table with data as empty is the worst failure this panel could have, so the row count is
not optional garnish — it is what makes the readout true. And once inlined rows are visible, Flush
becomes decidable on the same evidence Compact does.

### Where the row count comes from, and the bug that decided it

The first implementation derived live rows from `ducklake_table_stats.record_count`, corrected by the
live delete files: `record_count - sum(delete_count)`. That reproduced `SELECT count(*)` exactly on
every table in the test fixture, and it was **wrong**.

It reported four rows for a table plainly holding two. `record_count` fails as a live count in two
independent ways, and only one is fixable from the metadata:

1. It ignores merge-on-read deletes. Live delete files do account for those, so subtracting them
   works — for *filed* data.
2. It also counts **superseded inlined rows**, and those have no delete file at all. Verified on
   1.5.5: three inserts, one delete, and one update against an unflushed table leave
   `record_count = 4` with **zero** `ducklake_delete_file` rows. The tombstones live in the per-table
   `ducklake_inlined_data_*` staging table, whose name is assigned at run time — the very naming
   invariant 12 exists to avoid depending on.

The tests missed it because the fixture had deletes only against *flushed* data and inlined data only
*without* deletes. The combination that breaks was never exercised. It surfaced only because the
Storage tab, the eject manifest, and the change feed were all on screen disagreeing with each other.

Live rows now come from `count(*)`, in a single `UNION ALL` across the catalog's tables. The cost
objection did not survive measurement: `count(*)` over two million rows is **9 ms**, because DuckDB
answers it from Parquet row-group metadata rather than by scanning, and one statement keeps it to a
single round trip — so it still does not scale round trips with catalog size, the property
`CatalogBrowser` is careful about. Filed rows (`sum(record_count) - sum(delete_count)` over live
files) are still read from the metadata, and inlined rows are what `count(*)` has that Parquet does
not.

A regression test now covers exactly that combination, and every row-count assertion is made against
`count(*)` on the same table rather than against a literal.

Filtering DuckLake's internals turns out to be free here: `ducklake_table_info` reports only user
tables. That is asserted rather than assumed — the day it changes, the panel would start reporting the
metadata catalog as tenant data. (Separately, and not acted on here: on 1.5.5 `information_schema` no
longer leaks them either, so the explorer's filter from verified behaviour 2 is now redundant rather
than load-bearing.)

It also works against a **read-only share** (invariant 9), covered by a test. Reading file metadata is
a read, in the same sense eject is a read (invariant 15), so nothing here needs a writable attachment.

### Table detail

Selecting a table in the Storage tab opens a panel below it: the file list with sizes and delete-file
pairing, and an **as of** snapshot selector. `ducklake_list_files` takes an optional snapshot
parameter, so "what did this table's storage look like on Tuesday" is free — a differentiated answer
next to the DuckDB-family tools, which have no snapshot to select. Verified live: a table at five files
today showed the single compacted file it had at an earlier snapshot.

This is the honest version of "show me the Parquet": real paths, real byte counts, ordered by size so
the small-file problem is visible rather than inferred, and scoped to files the catalog actually
references.

Two presentation decisions worth recording, because the obvious versions are wrong:

- **Rows show the file name; the directory they share is named once in the panel header.** Every file
  in one table's list sits in the same directory, so repeating a 90-character path per row pushes the
  identifying part off the end. The full path stays in each row's tooltip.
- **Not `direction: rtl`.** The usual CSS trick for truncating a long path at the *start* reorders the
  leading slash to the end of the visible text, rendering `…/sessions/ducklake-….parquet/` — a path
  that does not exist. It looked plausible enough in review to ship; it is wrong, and the fix above
  avoids the bidi problem rather than tuning it.

A snapshot that predates the table's creation **raises** rather than returning an empty list — the same
trap verified behaviour 7 documents for the change feed. The endpoint forwards the engine's message as
a `400`, matching `GetChangesAsync`. An empty list would be a different and false statement: "this
table had no files then".

### The routes it needs

Both are reads and both declare `TenantData`, **not** `TenantOwner`. Maintenance is the owner's to
authorise because it destroys or exports (`Capability.TenantOwner`'s own documentation says so); knowing
how large a table is, is not. A reader credential that cannot press Compact should still be able to see
that Compact is needed.

| Route | Capability | Notes |
|---|---|---|
| `GET /api/tenants/{tenantSlug}/catalogs/{catalogName}/storage` | `TenantData` | Per-table rollup, with the flush and compaction advisories |
| `GET …/storage/files?schema=&table=&snapshot=&limit=` | `TenantData` | Per-file list. Encryption-key columns never projected |

Neither declares a capability explicitly: `TenantData` is the group default, so both inherit it the
same way `schemas` and `snapshots` do. They join the existing group in
[`LakehouseEndpoints.cs`](../src/Lakehold.Api/Endpoints/LakehouseEndpoints.cs), so they inherit
`LakeholdAuthorizationFilter` and with it invariant 19's subject-before-capability ordering and the
404-not-403 rule. Nothing new was needed in the authorization layer; that is the point of the Phase 1
refactor `MCP.md` records.

**Schema and table are query parameters, not path segments.** The specification proposed
`…/storage/tables/{schema}/{table}/files`; a table name may contain a dot or a slash, and encoding
those into the path invites every router and proxy between the engine and the browser to disagree
about what the name was.

**The advisory threshold lives in the API layer**, not in the engine and not in the browser, so one
place owns it and a second consumer — an agent tool, a CLI — reaches the same verdict as the workbench.
The response carries `advisoryFileSizeBytes` alongside the verdict so a caller can see the basis of the
advice rather than having to trust it.

The engine side sits in `src/Lakehold.Engine/Catalog/StorageBrowser.cs`, beside `CatalogBrowser` — the
same kind of thing, a read-only projection of catalog state.

### Catching the UI up with the API

Independent of everything above, and cheaper: five surfaces existed server-side with no client method
in [`lakehouse.service.ts`](../web/lakehold-ui/src/app/lakehouse.service.ts) at all — **backups**,
**ejects**, the **change feed**, **CDC subscriptions**, and **scheduled maintenance runs**. Eject in
particular is the feature the comparison matrix calls uniquely ours, and it had no UI whatsoever.

All five now have one (Phase 4). Two placement decisions worth recording:

- **The change feed and its subscriptions share one tab.** Reading changes and being pushed them are
  the same question asked two ways, and a subscription's `lastDeliveredSnapshot` only means anything
  next to the snapshots the feed is showing.
- **Eject gets its own tab rather than a section under Backups.** Both write a copy of the catalog,
  but they answer different questions — a backup is how you recover *in place*, an eject is how you
  *leave* — and burying the differentiated one inside the commodity one would be an odd thing for this
  product to do.

The remaining matrix concession stands: *data sharing — read-only attach, **no UI***. A read-only
share still cannot be attached from the workbench.

## What is deliberately not built

In the spirit of `POSTGRES-WIRE.md`'s equivalent section.

| Not built | Why |
|---|---|
| Lineage graph | Unity Catalog's governance moat. It needs a query graph LakeHold does not collect, and it is not why a self-hoster chooses this product |
| Usage insights — top readers, query patterns | Same. Query history already answers the narrow version |
| Grants / permissions editor | Roles and tokens are administered through the API and `AUTHENTICATION.md`. A UI that mints credentials is a surface worth designing deliberately, later, not as a corner of the workbench |
| File upload / "add data" ingest UI | Managed ingestion is on the roadmap as connectors, not as a drag-and-drop |
| Notebook / multi-cell interface | The IDE is deliberately focused. A notebook is a different product decision |
| A raw object browser | [Above](#why-not-a-file-browser) |

## Phases

Each leaves the product working and is independently testable.

**Phase 1 — read the metadata. Landed.** `StorageBrowser` over `ducklake_table_info` joined to the
metadata catalog, plus the `/storage` route and `LakehouseOptions.CompactionAdvisoryBytes`. Seven
engine tests, including the read-only share and the inlined-only table.

**Phase 2 — the Storage tab. Landed.** The per-table rollup beside Results / Query history / Data
history with both advisories. Verified live, and this is the phase that proves the document's central
claim: pressing
**Flush** moved `staging` from *0 files / Flush pending* to *1 file / 712 B / no advisory*, and pressing
**Compact** merged `sessions` from *6 files, 8.8 kB average, Fragmented* into *1 file, 18 kB, no
advisory*. The panel refreshes itself after either, because a maintenance readout that does not show
the effect of the button beside it is not a readout.

**Phase 3 — table detail. Landed.** `ducklake_list_files` behind `/storage/files`, the per-file panel,
the as-of snapshot selector, truncation reporting. Five more engine tests.

**Phase 4 — catch up with the API. Landed.** Four more tabs — **Changes**, **Backups**, **Eject**,
**Schedule** — covering all five surfaces that had no client method: backup generations with restore,
eject bundles with their attested table lists, the row-level change feed, webhook subscriptions, and
the scheduled-run log. Sixteen client methods where there were seven.

Verified live: the change feed renders an update as its pre-image and post-image pair; a restore
rebuilt 31 tables into a new catalog and then **refused the same target on a second attempt**,
surfacing the engine's own refusal verbatim; an eject bundle expands to its per-table SHA-256
attestation. Three things the panels forced into the open:

- The error banner said **"Query failed"** for every failure in the workbench. With one panel that was
  true; with eight it labelled a restore refusal and a webhook rejection as SQL errors. Failures now
  carry the operation that produced them.
- A failure from one panel **stayed on screen** when the operator switched tabs, so a restore refusal
  hung over the eject list implying the eject had failed. Errors are cleared on tab change.
- The workbench component's stylesheet went over its 8 kB budget, which was the first sign the
  component had outgrown itself. Fixed properly by the split below rather than by raising the budget.

## The split

**Landed after Phase 4, and it is what the budget warning was really telling us.** One component had
grown to roughly 900 lines of TypeScript, 800 of template, and 840 of CSS, owning eight panels.

The seam is one component per tab:

| Component | Owns |
|---|---|
| `saved-queries-panel` | Reusable-query authoring, optimistic revisions, read-only execution, and the explicit published-view lifecycle |
| `data-history-panel` | Snapshot timeline, exact-commit changes, range comparison, bounded historical preview, restore plan and confirmation |
| `change-grid` | The shared dynamic row-change rendering and update-pair semantics |
| `storage-panel` | The footprint rollup, the per-file detail, the as-of selector |
| `changes-panel` | The change feed and its webhook subscriptions |
| `backups-panel` | Generations and restore |
| `eject-panel` | Bundles and their attestations |
| `schedule-panel` | The scheduled-run log |
| `panel-error` | The failure banner all of them share |
| `panel-shared.css` | The table, control-strip, and button chrome they have in common |
| `format.ts` | Display formatting plus SQL-standard identifier quoting for catalog-derived names |

The workbench keeps the chrome — selectors, maintenance buttons, credential popover, editor, tab strip
— plus the two panels tied to running a statement: results and query history. Data history owns its
requests and panel-local failures like the other operational surfaces.

Saved queries deliberately span the two architectural planes without merging them. Name,
description, SQL, revision, and publication metadata live in `ControlPlaneContext`, bound to one
catalog. Execution resolves the persisted definition by id and attaches that catalog read-only.
Publishing is an editor/owner operation that creates a DuckLake view with allow-listed identifiers;
the first publish refuses an existing object, while republish can replace only the target already
recorded for that definition. Updating SQL advances the authored revision but leaves the published
revision unchanged, making contract drift visible rather than silently changing downstream results.
A record-wide concurrency stamp is claimed inside a control-plane transaction before view DDL, so
publish, unpublish, edits, and deletes cannot race their durable effects. A failed metadata
finalisation reconciles the live target before returning a conflict.

Three things the split bought beyond size:

- **Stale errors became structurally impossible.** Each panel owns its banner, destroyed with the
  panel. The `error.set(null)` that had to be remembered on every tab change is gone.
- **So did stale per-catalog state.** Panels take the catalog as an input, cancel their outstanding
  list and mutation subscriptions on change, and reload. That retired a `clearCatalogPanels` method
  that had to be kept in step with every signal ever added without allowing a late response from the
  previous catalog to repopulate the new one.
- **The shared stylesheet is a real deduplication**, not a move: Angular's view encapsulation means a
  panel cannot inherit the workbench's styles, so without `panel-shared.css` each of the five would
  have carried its own copy of the table look.

`viewChild` is how the workbench still reaches a panel after a maintenance operation commits. Only the
visible panel exists — the strip is a `@switch` — so the reference is usually undefined, which is
exactly right: a panel that is not on screen reloads when it is next shown.

**One bug, introduced and caught during the refactor.** Each panel reloads from an `effect` reading
its `tenant` and `catalog` inputs. In `storage-panel` that effect also called `reload()`, which reads
`selectedTable` — making it a dependency. Opening a table then re-ran the effect, which closed it
again, so the detail panel never appeared. The fix is `untracked()` around the effect's body so it
depends on exactly the two inputs; all four panels use that shape now. Worth recording because it is
invisible to the type checker and to every build: only clicking the thing reveals it.

**Phase 5 — unified table inspector. Landed.** The catalog explorer and Storage rollup now open the
same Overview / Files / Columns inspector. Overview combines the logical schema, the already-landed
storage figures, and DuckLake's current and historical partition specifications. Views remain
inspectable but make no physical-storage claim.

**Phase 6 — live column profiles. Landed.** A profile is computed only when Columns opens, and a
distribution only when one column is selected. The profile reads the logical table — including
inlined rows and excluding merge-on-read deletes and superseded updates — rather than presenting
physical file statistics as current truth. Numeric and temporal columns use bounded equal-width
ranges; categorical columns use bounded top values; complex types say explicitly that no
distribution is available. Both profile and distribution accept the same as-of snapshot selector
as the file list.

**Phase 7 — unified data history. Landed.** The former snapshot ledger is now a table-oriented
history browser. It shows commit messages and schema-version transitions; reads rows inline at any
snapshot; drills into exactly one commit's row-level changes; and compares two table states through a
bounded change range. The range deliberately starts at `baseline + 1` because
`ducklake_table_changes` is inclusive at both ends. Historical browsing fetches one sentinel row past
the 500-row display ceiling so a bounded prefix can never be presented as a complete result.

Restore is a server-owned dry-run/confirm workflow, not generated mutation SQL. The plan reports live
and historical row counts, shared columns, current-only columns that receive current defaults or
nullability, and historical-only columns that will be ignored. Apply stages historical rows before
deleting anything, inserts through the current table definition, and runs under the Duckling gate in
one labelled transaction. Current defaults, nullability, and constraints therefore remain in force,
and any incompatibility rolls the entire operation back. Apply also requires the current snapshot id
returned by the reviewed plan; if another commit lands between review and confirmation, the server
refuses and asks for a fresh plan rather than applying stale assumptions. Read-only users retain every
browse and comparison action without seeing restore. The live Changes tab and Data history share
`change-grid`, so dynamic columns, truncation, and update pre/post-image presentation cannot drift.

Catalog names are kept as structured schema/table references. SQL generation escapes both identifiers
with SQL-standard doubled quotes, while change-feed table-function arguments use escaped string
literals rather than the bare-identifier allow-list. Names containing dots, reserved words, hyphens,
or quotes therefore target the object selected in the catalog across Browse, Changes, Compare, and
Restore.

## Test plan

Two suites: `tests/Lakehold.Engine.Tests/` for the engine, following `CatalogBackup`'s precedent, and
`web/lakehold-ui/src/app/*.spec.ts` for the panels.

### The engine

`tests/Lakehold.Engine.Tests/StorageBrowserTests.cs` uses one catalog carrying every awkward storage
case at once: a 200k-row table with 5k rows deleted, a three-row table in a non-`main` schema that is
entirely inlined, and — added after it caught a real bug — a table deleted *and* updated while still
inlined.

**Behaviour**
- Every table's reported row count equals `SELECT count(*)` on that table. Asserted against the query,
  never against a literal — a literal would prove the arithmetic is stable, not that it is right.
- Deleted rows are subtracted rather than counted: 200k written, 5k deleted, 195,000 reported.
- **Rows deleted while still inlined are not counted** — the regression above, pinned.
- A table holding only inlined rows reports zero files **and** its rows, so it is distinguishable from
  an empty one.
- Flushing moves rows out of `InlinedRows` and into a file, and the average file size then equals the
  single file's size.
- `ducklake_*` internals are absent from the rollup.
- `target_file_size` is null until the catalog sets one, and reads back as 5,000,000 after
  `ducklake_set_option(…, '5MB')`.
- The file list pairs each data file with its delete file, reaches a non-`main` schema, and reports
  truncation — but *not* at the boundary where the count exactly matches the limit, since nothing is
  missing there.
- A snapshot predating the table raises rather than returning empty.
- A **read-only attachment** answers the rollup, at the right row count.

`TableRestoreTests.cs` proves that planning changes nothing; apply preserves current defaults and
nullability across schema drift; an insert that violates the current definition rolls back the prior
delete and releases the shared session; a plan with no shared columns refuses before mutation; and an
intervening snapshot invalidates the plan. `ChangeFeedTests.cs` exercises dotted, hyphenated, reserved,
and embedded-quote table names against the real DuckLake table function rather than a browser fake.

**Security**
- `DataFileInfo` carries no property whose name contains "Key" or "Encryption". Asserted on the
  record's shape by reflection, not on a sample value, so an encrypted catalog cannot regress it
  silently.

### The panels

The component suite runs with `npm test --prefix web/lakehold-ui`. The harness did not exist
before: the scaffolding left a `tsconfig.spec.json` already pointing at `vitest/globals`, so wiring it
up meant adding a `test` target on `@angular/build:unit-test` with the `vitest` runner and installing
`vitest` and `jsdom`. No `browsers` entry — the panels are DOM-and-signals, and a real browser would
buy nothing for the cost.

`test-doubles.ts` holds a `FakeLakehouseService` that answers from memory and **records the arguments
it was called with**, which is where several of the interesting assertions live. It imports nothing
from the test runner, so it type-checks under the app config too; it is excluded from
`tsconfig.app.json` so it never enters the app program.

| Spec | Covers |
|---|---|
| `format.spec.ts` | Decimal units, the null-vs-zero distinction, and SQL-standard identifier escaping |
| `data-history-panel.component.spec.ts` | Timeline context, safe identifier quoting, sentinel-bounded historical browse, exact-commit drill-down, bounded comparison, atomic restore plan/confirm, context-switch invalidation, read-only behavior, panel-local failures |
| `panel-error.component.spec.ts` | Renders nothing without a message; preserves the engine's layout verbatim |
| `storage-panel.component.spec.ts` | **The `untracked` regression**, reload-on-catalog-change, all four advisory states, the threshold note, rollup-vs-file-list error headings |
| `changes-panel.component.spec.ts` | Feed not read unasked, structured awkward-name references, dynamic columns, update pre/post-image styling, subscription create and two-step delete |
| `backups-panel.component.spec.ts` | No restore offered for an incomplete generation, the proposed target, the refusal forwarded verbatim |
| `eject-panel.component.spec.ts` | No dry run, the history flag, bundle expand/collapse, incomplete marked untrusted |
| `schedule-panel.component.spec.ts` | Instance-wide run-log loading, scoped row rendering, success/failure states, and error-vs-empty truthfulness |
| `workbench.component.spec.ts` | First-run sign-in and tenant/catalog/token provisioning, views excluded from pickers, error cleared on tab change, dry-run then apply, panel refresh paths, and Data history integration |

**Four assertions were mutation-tested** — a green test that cannot fail is worse than no test, because
it reads as coverage. Reverting `untracked()` fails two storage-panel tests; removing the
`error.set(null)` in `showTab` fails the leak test; removing `storagePanel()?.reload()` fails the
maintenance-refresh test.

That exercise **found a weak test of my own**. "Does not keep the secret" originally asserted the
password field was gone after submitting — which passes whether or not the value was cleared, because
submitting closes the form and removes the input either way. It now reopens the form and reads the
field, and it fails when `secret.set('')` is removed.

### The routes

`tests/Lakehold.Api.Tests/StorageEndpointsTests.cs`, **sixteen tests**, against a real catalog rather
than a stubbed service. Two handlers moved from `private` to `internal` to be reachable, which is the
same shape `AdminEndpoints` and `GetScheduledRuns` already use.

`TableRestoreEndpointsTests.cs` covers the unversioned plan/apply DTO boundary with a real
awkwardly-named DuckLake table, including row counts, the optimistic snapshot precondition, applied
row state, and a preserved current default.

The advisories are why this file exists. `StorageBrowser` deliberately reports figures and no
verdicts — the threshold lives in the API layer so a second consumer, an agent tool or a CLI, reaches
the same conclusion as the workbench. That moves the only *interpretation* in the feature up a layer,
and untested interpretation is where the panel starts lying to an operator about whether a button is
worth pressing.

- Every table appears with its figures, and an inlined-only table reports zero files *and* its rows.
- Flushing clears the advisory it raised — the panel has to answer to the button beside it.
- A single file is never called fragmented, however small.
- Several small files are, and **the same table judged twice** proves the threshold is load-bearing:
  lowering the catalog's `target_file_size` below the files it already has retracts the advice. Without
  that, `advisoryFileSizeBytes` would be decoration on a verdict reached some other way.
- An unset target falls back to the configured floor and says so; a catalog that sets one wins.
- The file list pairs delete files, reports truncation — but not at the boundary where the count
  exactly matches the limit — and clamps a nonsensical limit rather than failing an operator's panel.
- An unknown catalog is a `404` from both routes, and **another tenant's catalog is a `404` too**, not
  someone else's storage.
- An unknown table and a snapshot predating the table are each a `400` carrying the engine's message.
- `DataFileDto` carries no property whose name contains "Key" or "Encryption", asserted by reflection.

**Three mutations, all caught.** Dropping the `FileCount > 1` guard fails the single-file test;
ignoring the catalog's own target fails two threshold tests; removing the limit clamp fails the
invariant-6 test.

### The configurations a local fixture cannot imitate

`tests/Lakehold.Engine.Tests/StorageBrowserIntegrationTests.cs`, four tests, gated on the same
variables the backup suites use and skipped without them.

Both differences land squarely on this surface. The rollup joins four `ducklake_*` metadata tables
through an alias discovered at run time, and **PostgreSQL is the configuration where DuckLake attaches
nothing queryable behind the catalog** — an alias that resolves for a local file proves nothing about
one that does not. The file list reports *paths*, and an object store returns URIs where a local
catalog returns filesystem paths.

- **S3.** With the data in a bucket, the rollup is still correct — row counts come from `count(*)`,
  which reads Parquet footers over the network here — and the file list returns `s3://` URIs, delete
  file included. Verified, not assumed: DuckLake reports full URIs, not bucket-relative keys.
- **PostgreSQL.** The alias resolves, the rollup is correct including a non-`main` schema read from
  `ducklake_schema` in the PostgreSQL catalog, and the metadata catalog's own tables never appear as
  tenant data.

One structural fix came with them. Every PostgreSQL-backed suite resets the same database's `public`
schema on the way in, and xUnit runs test classes in parallel by default, so they now share one
collection. Without it the two would have raced intermittently in a way that reads as a product bug.

## Documentation obligations

- ✅ This document records what landed, as `AUTHENTICATION.md` does.
- ✅ `AGENT.md`'s repository map names `docs/UI.md`.
- ✅ `ARCHITECTURE.md`'s matrix gains a **Storage / table detail UI** row and a
  **Column profiling UI** row, both reading ✅ now that the inspector and live profiles have landed.
  The *data sharing* row's "no UI" caveat is still accurate and stays. Three neighbouring rows were
  stale and were corrected with it: authentication, SSO/OIDC, and RBAC all read ❌ while the prose
  three lines below said the opposite, `AUTHENTICATION.md` records every phase as landed, and the code
  is in `src/Lakehold.Api/Auth/`. Authentication reads ⚠️ rather than ✅ for the reason the prose
  already gives — `RequireAuthentication` still defaults to false.
- ✅ `web/lakehold-ui/src/app/docs.content.md` gains sections for all five new panels and for the
  storage routes. It is the single source for the in-app page and the GitHub guide, so there is one
  place to edit, not two. The heading *Data operations beyond the workbench* was itself made false by
  this work — eject, backups, and CDC all have panels now — and is now *Data operations in depth*.
- ✅ Any new route is added to `web/lakehold-ui/public/sitemap.xml` — not applicable, since every panel
  lives inside `/workbench`, which is `noIndex`.
- ✅ **Split the workbench component**, one component per tab. See [The split](#the-split).

## What the review found

Four defects, each fixed with a test written red first.

**1. The rollup could not name a table DuckLake was happy to store.** `CountRowsAsync` built its
`count(*)` terms with `SqlIdentifier.Quote`, which *validates* a bare identifier and returns it
unquoted rather than escaping it. A catalog holding `order-items` or `my.table` therefore raised
`ArgumentException` — and because `GetStorageAsync` catches only `CatalogNotFoundException`, that
surfaced as a `500` and took the whole Storage panel down, not one row of it. A table called `select`
was the same bug wearing different clothes: it passes validation and produces a syntax error.

The irony is that the rest of the feature already knew such names exist — it is exactly why the file
list takes the table as a *query parameter* rather than a path segment. The workbench now keeps
schema and table as a structured reference all the way through its Data history and Changes pickers,
so it never has to recover those parts by splitting a display label. `SqlIdentifier` gained
`QuoteName`, which escapes rather than validates, and the two are now a deliberate pair: `Quote` for
a trust boundary where a malformed name should be rejected, `QuoteName` for a name that came out of
the catalog and is going back into a statement. The file list never had the problem — a table
function takes its table as a string literal — and there is now a test saying so.

**2. A failure and a table list both survived a catalog change.** Each panel owns its error banner,
which is what made a stale error impossible across a *tab* change: the banner is destroyed with the
panel. A catalog change does not destroy the panel, so the same leak reappeared one axis over — a
restore refusal from one catalog standing over another, and worse, one catalog's rows still listed
under a different catalog's name. All four input-driven panels now clear both in their effect. For
backups and ejects this is more than cosmetic: a Restore… offered against the wrong catalog rebuilds
the wrong thing, and an eject bundle shown under a catalog it does not attest to is the one thing an
attestation must never do.

**3. `formatTime` rendered a bare clock time.** Fine when it only labelled query history from the
current session; wrong for the panels that came later. Backup generations, eject bundles, snapshots
and the scheduled-run log all span days, and *did last night's backup run* cannot be answered by a
reading that makes yesterday and today identical. The date now appears once the timestamp is no
longer today's; today stays terse, because that is the common case.

**4. `schedule-panel` had no spec.** Every other panel had one. Five tests now, including that a
failure to *read* the log is shown rather than rendered as an empty log — silence there says "the
scheduler has never run", which is a different and much more alarming statement than "this credential
cannot see it".

One thing deliberately **not** changed: `ChangeFeed` validates its schema and table with `Quote` and
so refuses the same awkward names. That is a pre-existing trust boundary on a different surface,
reached by a different route, and widening it is a decision about the change feed rather than a
consequence of this work.

## Open questions

Answered, by measurement rather than by argument:

- ~~The restore form proposes a bare filename, and the result does not say where it landed.~~
  **Fixed, and it was a server change.** A relative target used to resolve against the API process's
  working directory, so a restore run from the workbench wrote the rebuilt catalog next to the binary
  rather than beside the catalog it came from — found by discovering one in the repository after
  testing. `LakehouseService.RestoreBackupAsync` now anchors a relative target to
  `LakehouseOptions.MetadataRoot`, which is where provisioning puts every catalog's metadata file and
  therefore what a bare name already means; an absolute path is still used as given. `CatalogRestore`
  resolves once up front so the refusal message, the directory created, the `ATTACH`, and the reported
  result all name the same file, and the result is always absolute. A restore that succeeds and leaves
  the catalog somewhere unfindable is a bad outcome for the one operation whose entire purpose is
  recovery. Four tests in `RestoreTargetTests`, one of which pins that anchoring did **not** open a way
  past the refusal to overwrite: the second attempt at the same bare name must resolve to the same file
  and be refused there. Mutating the anchor away fails two of them — and leaves a stray `.ducklake`
  in the test output directory, which is the original bug reproducing itself.

- ~~What is DuckLake's `target_file_size` default?~~ **It is not knowable, and the design changed to
  suit.** The DuckDB setting `ducklake_target_file_size` reads NULL by default, and DuckLake's built-in
  default is exposed through no setting or metadata row. A per-catalog override *is* readable —
  `ducklake_set_option(…, 'target_file_size', '5MB')` persists `5000000` to `ducklake_metadata` — so the
  API reports the catalog's value when it has one, falls back to `CompactionAdvisoryBytes` when it does
  not, and returns *both* so the basis of the advice is visible. Unset is reported as unset rather than
  guessed. The floor is 16 MB in **decimal** megabytes, because DuckLake reads `'5MB'` as 5,000,000 and
  a floor rendering as "17 MB" beside a target written `5MB` reads as a bug in the units.
- ~~Does `ducklake_table_info` report inlined rows?~~ **No** — and it carries no row count at all. Both
  come from the metadata catalog, and the consequence is `InlinedRows`, the Flush advisory, and the
  correction recorded under [Storage tab](#storage-tab). This was the single most design-changing
  finding.
- ~~Tab or its own route?~~ **A tab.** It keeps the tenant and catalog selectors and the maintenance
  buttons in the same frame, which matters more than linkability for a panel whose entire purpose is
  informing the button next to it. Revisit if operators start wanting to share a link to one table's
  storage.
- ~~Pagination shape for the file list.~~ It follows the `Truncated` shape the change feed already uses
  rather than proving `PUBLIC-API.md`'s cursor convention early. Converting one surface to a convention
  no other surface speaks would leave two conventions, not one; they convert together.

Still open:

- ~~Whether partition information is worth surfacing.~~ **Yes.** DuckLake 1.0 supports identity,
  date/time, and bucket partition transforms plus partition-spec evolution. The inspector reports
  both the active layout and its snapshot-bounded history.
- **What the advisory floor should be.** 16 MB is a judgement, not a measurement, and the only number
  here with no evidence behind it. It wants calibrating against a real catalog before it is treated as
  more than a starting point.
- **Whether the rollup's coupling to the metadata schema should be pinned harder.** `StorageBrowser`
  joins four `ducklake_*` metadata tables. The tests would catch a schema change, but they would catch
  it as a failure to compile a query rather than as a clear message. `MetadataExporter` has lived with
  the same coupling for longer; if either ever breaks on a DuckLake upgrade, that is the moment to add
  a version probe rather than now.
