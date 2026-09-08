# Catalog branching — zero-copy branches, diffs, and agent sandboxes

A branch is a writable copy of a catalog that costs nothing until it diverges. Create one from any
retained snapshot in seconds, write to it without the parent ever noticing, read the difference as a
change feed, and either merge it back or throw it away. It is the feature that turns the MCP server
from "an agent can run SQL against production" into "an agent works on a branch and a person reviews
the diff".

Like [`AUTHENTICATION.md`](AUTHENTICATION.md), [`MCP.md`](MCP.md), and
[`LAKEBASE-PLAN.md`](LAKEBASE-PLAN.md), this is a specification and a running record, written to be
worked one phase at a time. Nothing here contradicts an invariant in `AGENT.md`; where a rule
already exists, this document says how branching preserves it rather than restating why.

**Status: not built. This document is the proposal.** The claim is carried publicly as *roadmap* — a
`🛠️ roadmap` cell in the `Catalog branching (git-style)` row of the
[`ARCHITECTURE.md`](ARCHITECTURE.md) capability matrix, and phase 6 of
[`LAKEBASE-PLAN.md`](LAKEBASE-PLAN.md), whose analytical half this document now owns; the
operational half stays there. Neither may say *shipped* before phase 1 below lands, and the matrix
may not say ✅ before phase 3.

DuckLake itself has no branching. The upstream RFC
([duckdb/ducklake#720](https://github.com/duckdb/ducklake/discussions/720)) proposes a `branch_id`
column on every metadata table and was still open, neither accepted nor rejected, when
[`COMPETITIVE-RESEARCH.md`](COMPETITIVE-RESEARCH.md) last checked it in June 2026. LakeHold does not
wait for it, and does not fork DuckLake to get it: the design below needs only what the metadata
tables already are. The section [*If DuckLake ships branching*](#if-ducklake-ships-branching) says
what changes when it does.

## What a branch is

Five properties do the work. Each one is a requirement the phases are tested against, not a slogan.

| Property | What it means here |
|---|---|
| Zero-copy | Creating a branch copies metadata rows, never Parquet. Cost is proportional to the size of the metadata catalog, not the data. |
| Isolated | A write on the branch produces new files under the branch's own prefix. The parent's metadata, snapshots, and files are never modified by anything the branch does. |
| Pinned | A live branch pins its fork snapshot in the parent, so platform maintenance on the parent cannot delete a file the branch reads. The branch's history begins at the fork, so there is no older history for the pin to miss. |
| Diffable | The branch's own commits are exactly the snapshots above the fork, so `ducklake_table_changes` from `fork + 1` to head *is* the diff. No second bookkeeping. |
| Disposable | Deleting a branch drops its metadata schema and its own prefix, and touches nothing else. It can never delete a parent file. |

The one property this design does **not** claim is *mergeable without limit*. Phase 3 ships a
fast-forward, data-only merge. Merging over a parent that has moved on, or merging schema changes,
is stated as out of scope in [*What stays out*](#what-stays-out) and why.

## Why this, and why now

Three reasons, in the order that matters.

**It makes the agent surface safe to demonstrate.** [`MCP.md`](MCP.md) withholds `execute` until an
operator enables writes, and puts maintenance behind a separate operator-commands switch, because an
autonomous agent writing to a production catalog is the thing a reviewer flinches at. A branch
removes the flinch: the agent is handed a credential that reaches only its branch, does whatever it
likes, and the person merges — or does not — after reading the change feed. Every piece of that flow
except the branch exists today.

**Nobody self-hosted ships it.** The [`ARCHITECTURE.md`](ARCHITECTURE.md) matrix gives branching to
Dremio and Trino-on-Nessie only, and Nessie is a separate JVM service with its own state. MotherDuck
has a zero-copy clone, which [`COMPETITIVE-RESEARCH.md`](COMPETITIVE-RESEARCH.md) marks ⚠️ on
purpose: it is a clone, not a branch with a diff and a merge. The upstream RFC sits in that
document's demand table at 12 reactions, behind file-format and partitioning asks and ahead of
everything else.

**The pieces are already here.** `MetadataExporter` copies a catalog's metadata tables under the
session gate. `CatalogRestore` loads such a copy into a fresh metadata store and verifies row counts
before it will call the result complete. `ChangeFeed` reads a snapshot range. `TableRestore` shows
how to change a table's rows inside one labelled transaction while preserving its definition, and
how a dry-run plan with an expected snapshot id makes an intervening commit force a fresh review.
`DucklingPool` already keys sessions by catalog identity. Branching is the story that joins them.

## Design

### A branch is a catalog

A branch is a `LakeCatalog` row with three extra columns: `ParentCatalogId`, `BranchName`, and
`ForkSnapshotId`, plus lifecycle state (`Active`, `Merged`, `Deleting`, `Broken`) and an optional
`ExpiresUtc`. Everything downstream follows from that choice:

- **The engine attaches it exactly as it attaches any catalog.** The branch's descriptor names the
  branch's metadata schema (or file) and the branch's data path. No new attach mechanics.
- **It attaches under the parent's name.** A session on branch `feature-x` of catalog `analytics`
  writes `analytics.main.orders`, so saved queries, views, and an agent's prompt work unchanged on
  either. The branch is selected by credential and route, never by a different SQL name, which is
  the same reasoning that stops route-level catalog selection in invariant 4.
- **Session isolation is free.** `DucklingPool` keys on tenant, catalog id, name, configuration
  version, and attachment mode. A branch has its own id, so it never shares a warm session with its
  parent, and `EvictReaders` for one leaves the other's readers alone.
- **Authorisation resolves to the root.** A catalog-narrowed token that reaches `analytics` reaches
  its branches; a branch is not a separate grant. `CapabilityPolicy` is given the root catalog for
  the subject check and the branch for the attachment, and the ordering in invariant 19 is
  unchanged: an unreachable parent is a 404 for its branches too.
- **Storage view and the table inspector work on a branch** because they work on a catalog. **Backup
  and eject do not get that for free.** `CatalogStorageNamespace.Under` derives a location from the
  tenant key and the catalog *name*, and a branch carries its parent's name, so an unqualified
  branch backup would land among the parent's generations and a later restore could offer it as one
  of them. A branch therefore refuses `backup` — it is disposable state whose durable form is the
  parent plus the diff — and an eject of a branch writes under
  `<EjectRoot>/<tenant-key>/<catalog>/branches/<branch>/` through a branch-aware overload of that
  helper. Eject re-materialises through the catalog (invariant 15), so inherited and branch-written
  files are indistinguishable in the bundle.

### The fork

Creating a branch is a metadata copy, taken under the parent's session gate so no write can land
partway through — the same reason `MetadataExporter` holds it.

**PostgreSQL metadata.** Every PostgreSQL-backed catalog already owns one schema in the metadata
database (`lh_<tenant>_<hash>`, created by `DucklingSessionConfigurator`). A branch is another
schema in the same database, filled by `CREATE TABLE branch.t AS SELECT * FROM parent.t` for every
table `MetadataExporter` discovers — never a hard-coded list, for exactly the reason invariant 12
gives: the per-table `ducklake_inlined_data_*` tables hold committed rows that are not yet in
Parquet, and a fixed list would fork a catalog missing its newest writes. The copy runs through the
same privileged, read-only-on-the-parent metadata handle the exporter opens, and the credential it
needs is created and dropped under the gate as invariant 13 requires.

**Local-file metadata.** Export to Parquet with `MetadataExporter`, load into a new file with the
loader `CatalogRestore` already has. Slower than a schema-to-schema copy and confined to single-node
deployments, which is the profile local files are documented for.

**The copy is then cut to the state live at the fork, and four rewrites are applied.** Together they
are what makes a branch different from a restore:

1. **History begins at the fork.** Snapshot and snapshot-change rows below the fork are dropped, and
   so is every row whose `end_snapshot` is at or below it — a file, column, or table version already
   dead at the fork. What remains is exactly what `ducklake_expire_snapshots` would leave, without
   scheduling anything for deletion, and DuckLake reads it as an ordinary catalog whose first
   snapshot is the fork (verified below). A query below the fork fails cleanly with *no snapshot
   found* instead of reading a file the parent has since deleted, and the pin has nothing older to
   miss.
2. Every inherited `ducklake_data_file` and `ducklake_delete_file` row has `path_is_relative` set to
   false and its `path` replaced by the resolved absolute location — data path, then the schema's
   path, then the table's path, then the file's. The branch reads the parent's Parquet by absolute
   reference; new files it writes are relative, under its own data path.
3. `ducklake_schema` and `ducklake_table` carry `path` and `path_is_relative` too, and they are left
   relative so that new files resolve under the branch's data path. **A parent whose schema or table
   path is absolute cannot be forked**: an inherited absolute table path would send the branch's
   writes into the parent's prefix. The fork checks and refuses, naming the table, rather than
   rewriting a path a tenant chose.
4. The `data_path` key in `ducklake_metadata` is set to the branch's data path, and the parent's
   `ducklake_files_scheduled_for_deletion` rows are not copied — they are the parent's pending
   deletions, and a branch must never hold a row that names a parent file for deletion.

The branch's own commits continue from `fork + 1`, so `ducklake_table_changes` from there to head is
the diff, with no second bookkeeping.

**Fork at head** is phase 1. **Fork at an older retained snapshot `S`** is phase 2 and is a
truncation of the same copy: drop every row whose `begin_snapshot > S`, null every `end_snapshot >
S`, and drop snapshot rows above `S`, across every table that carries those columns. Tables without
them describe the head, not `S`, and must be rebuilt: `ducklake_table_stats` and
`ducklake_table_column_stats` from the per-file statistics of the files live at `S` — a head-derived
minimum or maximum can exclude a value present at `S` and prune a file wrongly — with `next_row_id`
kept at the parent's head value, because a row id only has to be unique, and
`ducklake_schema_versions` cut to versions at or below `S`. Whether DuckLake rebuilds absent table
statistics itself is spike 8's question. The result is verified the way a restore and an eject are
verified (invariants 12 and 16): every table's row count on the branch must equal the parent's `AT
(VERSION => S)` count, and the branch is not marked `Active` until it does. A verification failure
deletes the half-made branch rather than leaving a catalog that looks complete.

### Storage layout

A fifth root joins the four that `Lakehouse:StateRoot` resolves in
[`POSTGRES-AND-STORAGE.md`](POSTGRES-AND-STORAGE.md):

```text
<BranchRoot>/<tenant-key>/<catalog>/<branch>/
```

`BranchRoot` is a **sibling** of `DataRoot`, never a child, for the reason invariant 11 gives for
`BackupRoot`: DuckLake's orphan sweep treats anything under a catalog's data path that the catalog
does not reference as garbage, and a branch's files are precisely that from the parent's point of
view. Nested under the data path, a branch would delete itself the first time the parent ran an
orphan cleanup.

The branch session needs read access to the parent's prefix and read-write access to its own. Under
invariant 8 that is two generated DuckDB secrets, each scoped to one prefix, from the same storage
profile the parent uses. The credential does not distinguish read from write — DuckDB secret scoping
is by path — so what stops a branch writing into the parent's prefix is that its metadata never
references a relative path there, not the secret. The parent's own session is unchanged: it holds no
secret for the branch prefix and cannot reach it.

### The deletion problem, stated precisely

[`LAKEBASE-PLAN.md`](LAKEBASE-PLAN.md) names the gate — "branching cannot ship before file
references are counted across every schema that shares a data path" — and this design meets it by
making sure that **no two schemas share a data path that either can delete from**. There are three
ways a deletion can cross the boundary, and each has a specific answer.

| Hazard | What would go wrong | Answer |
|---|---|---|
| Parent orphan sweep deletes branch-written files | `ducklake_delete_orphaned_files` on the parent removes everything under the parent's data path it does not reference. | Branch files never live there. `BranchRoot` is a sibling. |
| Parent expiry deletes files the branch inherited | The parent compacts or deletes rows, closes the inherited files' `end_snapshot`, expires the snapshots that referenced them, then `cleanup` removes them. The branch now has absolute references to files that do not exist. | **A live branch pins its fork snapshot.** `LakehouseMaintenance.ExpireSnapshotsAsync` clamps `olderThan` to the commit time of the oldest fork snapshot of any live branch, so that snapshot is retained, and its dry-run output says so. DuckLake only schedules a file for deletion once no retained snapshot references it; a file live at `S` is referenced by `S`. Verified below, in both directions. |
| Branch cleanup deletes parent files it superseded | The branch deletes rows or compacts, an inherited file gets `end_snapshot` set *in the branch's metadata*, branch expiry and cleanup then delete a file **under the parent's prefix** by absolute path. | **A branch refuses `expire` and `cleanup`** with a 409 that says why. Branch storage is reclaimed by deleting the branch, which drops the schema and deletes the branch prefix and nothing else. |

The refusal in the third row is the honest version of reference counting for a first release: a
branch is short-lived, and the space it can waste is bounded by its own writes. Phase 5 replaces the
refusal with real counting — filter `ducklake_files_scheduled_for_deletion` to the branch's own
prefix before `cleanup` runs — once there is evidence that branches live long enough to need it.

What this does **not** cover, and where the limit is written down: tenant SQL can call
`ducklake_expire_snapshots`, `ducklake_cleanup_old_files`, and `ducklake_delete_orphaned_files`
directly. Invariant 4 rules out parsing SQL to stop it, and this document does not try. Two things
bound the damage. First, on the platform maintenance path — HTTP, MCP, and the Workbench — the pin
and the refusal always apply. Second, every branch has an **integrity check**: list the parent's
prefix once — the way `StorageBrowser` already does with `glob` — and count the inherited absolute
references that are not in the listing. One listing request per thousand objects, never a request
per file. It must read the listing rather than run a query: `count(*)` on a DuckLake table is
answered from file metadata and returns the right number after every file it describes has been
deleted (verified below). It runs on demand, on schedule, and before a merge, and a branch that
fails it is marked `Broken` and reported as such rather than serving errors one table at a time. A
broken branch cannot be merged. The same check on the parent side reports which live branches a
proposed expiry would strand, so the dry-run is the warning.

### The diff

Two levels, both read-only and both available to a reader.

**Table level.** For every table, the number of inserts, deletes, and updates on the branch since
the fork, from `ducklake_table_changes(branch, schema, table, fork + 1, head)` aggregated by
`change_type`; plus tables created or dropped on the branch, and schema changes, from
`ducklake_snapshot_changes.changes_made` for the same range. Bounded by table count, not row count,
so it is safe as a summary.

**Row level.** A page of the existing `ChangeFeedPage` for one table over the same range, with the
existing cursor and row ceiling. Nothing new: this is `ChangeFeed.ReadAsync` given the branch's
Duckling and the fork as a lower bound. An update appears as its pre-image and post-image pair, so a
review can show old and new values without a second query.

A diff of a `Broken` branch reports the break and returns nothing else.

### The merge

Phase 3 ships one merge, chosen for being provably correct rather than general:

- **Fast-forward only.** Fast-forward means the parent has made **no data or schema change** since
  the fork. It is decided from `ducklake_snapshot_changes.changes_made` over the parent's post-fork
  snapshots, not from snapshot-id equality: a `lakehold maintenance:` flush or compaction commit
  changes no row and must not block a merge, or a scheduled flush would refuse every branch older
  than an hour. If the parent has advanced with a real change, the merge is refused with the
  parent's head, and the answer is a new branch from head. A merge over a moved parent needs a
  three-way merge on the metadata, which is the upstream RFC's problem and not one to solve with a
  partial answer.
- **Data only.** A branch whose `changes_made` includes a schema change is refused. Column
  additions, drops, and type changes need the definition-preserving reasoning of invariant 22
  applied to two definitions at once, and that is phase 5 work with its own verification. **Creating
  a table on the branch is a schema change too** and blocks the phase 3 merge, which matters because
  it is the commonest thing an agent sandbox does; it is the first thing phase 5 lifts, because a
  new table has no parent definition to reconcile.
- **One labelled transaction.** Apply runs under the parent's gate inside `lakehold merge: <branch>
  @ <snapshot>`, so a platform-initiated snapshot stays distinguishable from a tenant's writes
  (invariant 10) and any failure rolls the whole merge back.
- **Plan, then apply.** As with `TableRestore`, the API returns a dry-run plan — the table-level
  diff, the parent snapshot it was computed against — and `apply: true` requires that snapshot id.
  An intervening parent commit turns the fast-forward check into a refusal rather than a stale
  apply.
- **Staged, per table.** For each changed table, the branch's rows are staged, the parent's rows are
  replaced through the existing table definition, and the row count is re-read and compared with the
  branch's before the transaction commits. Whether the replacement is per-row-id (the branch
  inherits the parent's row ids, so deletes can be addressed exactly) or whole-table is decided by
  the spikes in [*Verified engine behaviour*](#verified-engine-behaviour-required-first); the
  contract is the same either way. A per-row-id apply is a smaller commit, but it depends on row-id
  stability across the fork, which must be demonstrated rather than assumed.
- **Merge does not delete the branch.** A merged branch becomes `Merged`, keeps its diff readable
  for audit, and is deleted explicitly or by its expiry. It attaches read-only from then on: its
  `ConfigurationVersion` moves, so every warm session on it is evicted and reattaches read-only.
  Merging twice is refused.

The merge is a write to the parent, so it invalidates the parent's warm readers through
`EvictReaders` like any committing statement (invariant 20); the branch's sessions go with the
read-only transition above.

**Why not "promote".** The tempting alternative is to make the branch *become* the catalog by
repointing the parent's record at the branch's schema and prefix. It is refused because the promoted
catalog would then reference files under two roots forever, the old prefix would become a second
data path with nothing to sweep it, and every reader of the parent would need to reattach. A merge
that writes through the parent leaves one catalog with one prefix and one history.

### Agent sandboxes

This is the reason the feature is worth building first, and it is mostly wiring:

- **A token can be narrowed to a branch.** The existing catalog-narrowing on `ApiToken` gains a
  branch id. A branch-narrowed token reaches the branch and nothing above it; its `list_tenants` and
  catalog listing show the branch as the only catalog. A reader token cannot create a branch, so a
  read-only agent stays read-only.
- **MCP tools declare capabilities like routes** (invariant 21): `create_branch`, `list_branches`,
  `get_branch`, `diff_branch`, `delete_branch`, and `merge_branch`. `merge_branch` sits behind the
  operator tier with `apply_maintenance` and `apply_table_restore`, because it is the one that
  changes the parent. The MCP audit record already names actor, tool, tenant, and catalog; a branch
  is a catalog, so it is covered.
- **Say what the sandbox cannot do.** Until phase 5, a table the agent creates on its branch cannot
  be merged. The `create_branch` tool description and the Workbench diff both say so up front, not
  at the merge.
- **The human flow is the Workbench.** A branch switcher on the catalog, a diff view, and a merge
  button that shows the plan and demands the confirmation the API demands.

A "sandbox mode" on the token-minting endpoint — mint a credential, create a branch, narrow the
credential to it, return both — is a convenience over those primitives and ships in phase 4 once the
primitives have been used by hand.

### What stays out

Each is a decision with a reason, so it is not mistaken for an omission.

| Not in this plan | Why |
|---|---|
| Branch of a branch | A second-level fork pins a snapshot in a catalog that refuses expiry anyway, and reference counting across three schemas is phase 5's problem. Depth is one. |
| Branch of a read-only share | The share's files sit under another tenant's prefix, and the pin cannot reach into another tenant's maintenance. A writable fork of shared data is a copy, not a branch, and belongs with the sharing work. |
| Non-fast-forward merge | Needs three-way metadata merge with divergent id allocation. The upstream RFC identifies this as the reason branching belongs in DuckLake; LakeHold agrees and waits. |
| Schema-change merge | Two table definitions to reconcile; invariant 22 applied twice. Phase 5, with its own verification. |
| Branching the operational half | `LAKEBASE-PLAN.md` says why stock PostgreSQL cannot do it and what Neon would give. Unchanged, and out of scope here. |
| SQL syntax (`CREATE BRANCH`) | Branch selection is by credential and route, as catalog selection is. A SQL verb would reintroduce route-level selection in a new spelling. |

### If DuckLake ships branching

The RFC's shape is a `branch_id` on every metadata table with lineage rows for O(1) visibility. If
it lands, the fork becomes a lineage row instead of a schema copy, the pin becomes the engine's own
reference tracking, and the merge gains the three-way case. What survives unchanged is everything
above the engine: the `LakeCatalog` model, the routes and capabilities, the token narrowing, the
diff as a change-feed read, the plan-then-apply merge contract, the MCP tools, and the Workbench.
The design deliberately keeps the branch's *identity* in the control plane and its *contents* in
DuckLake so that swap is an engine change and not a product change.

## Verified engine behaviour required first

[`ARCHITECTURE.md`](ARCHITECTURE.md) keeps a *Verified engine behaviour* section because DuckLake
semantics are exercised, not inferred. These spikes precede phase 1 code; each has a pass condition,
and the result — with the DuckDB and DuckLake versions — goes into that section.

1. **Absolute inherited paths.** A metadata copy with inherited files rewritten to `path_is_relative
   = false` reads every row the parent reads, and an insert on the copy writes a relative file under
   the copy's `data_path`, on a local filesystem and on MinIO with two prefix-scoped secrets. Pass:
   row counts match and the new file's location is the branch prefix.
2. **`ducklake_metadata.data_path` is honoured** for new writes after the rewrite, without
   `OVERRIDE_DATA_PATH`. Pass: no object appears under the parent prefix.
3. **The pin works.** With the fork snapshot retained on the parent, deleting and compacting on the
   parent then running `expire` with `olderThan` clamped to the fork, followed by `cleanup`,
   schedules and deletes no file the branch references. Pass: the branch's integrity check reports
   zero missing files. Then the negative case: without the clamp, it does strand the branch. Pass:
   the check reports the missing files, proving the check itself.
4. **Branch-side compaction with mixed paths.** Compact on the branch after deleting inherited rows.
   Record which files land in `ducklake_files_scheduled_for_deletion` and confirm they include
   absolute parent paths — which is the evidence the branch-side refusal exists for.
5. **The diff is the change feed.** After three commits on the branch,
   `ducklake_table_changes(branch, s, t, fork + 1, head)` returns exactly those commits and `fork +
   1` on the parent returns the parent's own, unrelated commits or nothing.
6. **Row-id stability across the fork.** A row's `rowid` on the branch equals its `rowid` on the
   parent for inherited files, before and after a branch write to the same table, and after a
   compaction on the parent. Pass decides whether the phase 3 merge can address deletes by row id.
7. **Inlined data forks.** A parent with a small unflushed commit forks a branch that reads those
   rows, and `flush` on the branch writes them under the branch prefix.
8. **Fork at snapshot.** The truncation rule applied at `S` yields a branch whose per-table counts
   equal `parent AT (VERSION => S)`, on a parent whose history includes a table created after `S`, a
   table dropped before `S`, and a column added after `S`; and whether DuckLake rebuilds
   `ducklake_table_stats` and `ducklake_table_column_stats` when they are absent, or the fork must.
9. **PostgreSQL schema-to-schema copy** through the postgres extension under the privileged metadata
   handle, including the run-time-named inlined data tables, with the credential dropped before the
   gate is released.
10. **History cut at the fork.** Dropping the pre-fork snapshot rows and every row dead at the fork
    yields a catalog DuckLake attaches, reads, writes to, and diffs, and a query below the fork
    fails with *no snapshot found*.

### Verified so far

Run on 8 September 2026 with DuckDB v1.5.5, the `ducklake` extension at `d8a1881e`,
`postgres_scanner`, and `httpfs`; metadata in PostgreSQL 17 under one `METADATA_SCHEMA` per catalog,
data on MinIO. The fork was a `CREATE TABLE … (LIKE …)` plus `INSERT … SELECT` per table in `psql`,
followed by the rewrites above in SQL. Spikes 4, 7, 8, and the local-file half of 9 have not been
run.

| Spike | Result |
|---|---|
| 1 — absolute inherited paths | **Pass.** Default schema path `main/` and table path `<table>/`, both relative. The branch read all 600k parent rows and 100k of a second table; its insert wrote one relative `ducklake-<uuid>.parquet` under the branch prefix; the parent prefix held the same four objects before and after. |
| 2 — `data_path` honoured | **Pass.** Attached with `DATA_PATH` equal to the rewritten key; no `OVERRIDE_DATA_PATH`; no object appeared under the parent prefix. |
| 3 — the pin, clamped | **Pass.** After `CREATE OR REPLACE TABLE` on the parent closed every inherited file, `ducklake_expire_snapshots(older_than => <fork time>)` retained the fork, scheduled nothing, and `cleanup_old_files` deleted nothing the branch referenced. |
| 3 — the pin, unclamped | **Pass, in the sense that the failure is real.** Expiring the fork snapshot scheduled the three inherited files, cleanup removed them, and the listing-based check reported three missing. `count(*)` on the branch still returned 800000; `sum(id)` failed with HTTP 404. `ducklake_merge_adjacent_files` and `ducklake_rewrite_data_files` processed zero files on three 200k-row files with half their rows deleted, so supersession was forced with the replace; the pin's reasoning is the same either way. |
| 5 — the diff is the change feed | **Pass.** `ducklake_table_changes(branch, 'main', 't', fork + 1, head)` returned exactly the branch's 200000 inserts as snapshot `fork + 1`; on a second table a branch delete of ten rows appeared as ten `delete` rows. |
| 6 — row-id stability | **Pass, before compaction.** Zero `(rowid, id)` mismatches between parent and branch before and after a 200k-row branch insert; the new rows took 600000–799999. `next_row_id` then diverged — 600000 on the parent, 800000 on the branch — so two sides that both insert allocate the same row ids to different rows. That is the concrete reason a non-fast-forward merge cannot address rows by id. The compaction case has not been run. |
| 10 — history cut | **Pass.** A fork with one snapshot row, the fork's, and every dead row removed attached and read identical sums to the parent, accepted an insert and a delete under its own prefix, diffed from `fork + 1`, and refused `AT (VERSION => fork - 1)` with *no snapshot found*. |

## Delivery phases

Each phase is independently shippable and leaves the product working with the claim it makes.

### Phase 1 — fork at head, isolated writes, disposable

- [ ] `LakeCatalog` gains `ParentCatalogId`, `BranchName`, `ForkSnapshotId`, `State`, `ExpiresUtc`;
      migration; descriptor projection attaches under the parent's name with the branch's schema and
      prefix.
- [ ] `Lakehouse:BranchRoot`, resolved from `StateRoot` as a sibling, documented in
      `POSTGRES-AND-STORAGE.md` as the fifth root. Production Compose override in step.
- [ ] `CatalogBranch.CreateAsync`: metadata copy under the parent's gate, the history cut and four
      rewrites, refusal of an absolute schema or table path, verification by per-table row count
      against the parent's head, and deletion of a branch that fails it.
- [ ] A branch refuses `backup`; a branch eject writes under
      `<EjectRoot>/<tenant-key>/<catalog>/branches/<branch>/` through a branch-aware
      `CatalogStorageNamespace` overload.
- [ ] Two prefix-scoped secrets per branch session; parent sessions unchanged.
- [ ] Expiry pin: `ExpireSnapshotsAsync` clamps to the oldest live fork snapshot and says so in the
      dry-run detail. Branch sessions refuse `expire` and `cleanup` with a 409 naming the reason.
- [ ] Integrity check, on demand and scheduled; `Broken` state; the parent-side "would strand"
      report in the expiry dry-run.
- [ ] Delete: drop schema or file, delete the branch prefix, remove the row. Never touches the
      parent prefix, by construction and by test.
- [ ] Limits: maximum live branches per catalog (default 10), default expiry (14 days, extendable,
      refusing "never" for an agent-minted branch), depth one, no branching of a read-only or
      read-only-attached catalog.
- [ ] Routes under `/api/v1/tenants/{tenant}/catalogs/{catalog}/branches`, capabilities per the
      [table below](#api-and-capability-surface), `problem+json` codes, audit rows.
- [ ] Workbench: branch switcher on the catalog, create and delete, the state badge.
- [ ] `ARCHITECTURE.md` matrix cell moves from `🛠️ roadmap` to `⚠️ branches, no merge`.

### Phase 2 — fork at any snapshot, and the diff

- [ ] Truncation at `S`, statistics and schema-version rebuild, and the count verification against
      `AT (VERSION => S)`.
- [ ] Table-level diff from the change feed and `changes_made`; row-level diff as a `ChangeFeedPage`
      with the existing cursor and ceiling.
- [ ] Workbench diff view: table summary, expandable row pages, pre-image and post-image side by
      side.
- [ ] MCP read tools: `list_branches`, `get_branch`, `diff_branch`.

### Phase 3 — fast-forward merge

- [ ] Plan: fast-forward decided from `changes_made`, schema-change check including created tables,
      integrity check, table-level diff, parent snapshot id. Apply: that id required, one labelled
      transaction, per-table verification before commit, `EvictReaders` on the parent, `Merged`
      state with the branch reattached read-only.
- [ ] Row-id or whole-table apply, decided by spike 6 and recorded here.
- [ ] Workbench merge: plan, confirmation, outcome. MCP `merge_branch` behind the operator tier.
- [ ] `ARCHITECTURE.md` cell becomes `✅ branch, diff, fast-forward merge`; the README and `/compare`
      row is added with its evidence contract in the browser suite, and not before.

### Phase 4 — agent sandboxes and the public surface

- [ ] Branch-narrowed tokens; the sandbox convenience on the minting endpoint.
- [ ] MCP `create_branch` and `delete_branch` under the write gate; connection guidance in `MCP.md`
      for "give the agent a branch".
- [ ] Public API coverage per `PUBLIC-API.md` conventions and the four SDKs.
- [ ] `/docs` page section; `OPERATIONS.md` gains the pin and the storage bound.

### Phase 5 — reference counting and the wider merge

- [ ] Filter `ducklake_files_scheduled_for_deletion` to the branch prefix before `cleanup`; lift the
      branch-side refusal; consider depth two.
- [ ] New-table merge first — no parent definition to reconcile — then schema-change merge with
      two-definition reconciliation and its own verification.
- [ ] Re-check the upstream RFC; if native branching has landed, the engine swap described above.

## API and capability surface

| Route | Capability | Notes |
|---|---|---|
| `GET …/branches` | `TenantData` | Readers see branches; a branch-narrowed token sees its own. |
| `POST …/branches` | `TenantWrite` | Body: name, optional `fromSnapshot`, optional `expiresAt`. Editors and owners. A read-only token is refused. |
| `GET …/branches/{branch}` | `TenantData` | State, fork, head, expiry, integrity result. |
| `GET …/branches/{branch}/diff` | `TenantData` | Table level; `?table=` for a row page with cursor. |
| `POST …/branches/{branch}/merge` | `TenantWrite` | Plan by default; `apply: true` with `expectedParentSnapshotId`. |
| `DELETE …/branches/{branch}` | `TenantWrite` | Own scratch, own prefix. |
| `POST …/branches/{branch}/check` | `TenantData` | Runs the integrity check now. |

Merge is `TenantWrite` rather than `TenantOwner` on purpose: it commits rows the editor could have
committed with `INSERT` and `DELETE` directly, and it is not destructive to history. Expiry and
cleanup remain the owner's, and a branch refuses them regardless.

Every branch route resolves the subject against the **root** catalog first, so an unreachable parent
returns 404 for `…/branches/anything` (invariant 19). Every mutating route writes the audit row the
catalog routes write.

## Documentation obligations

- `AGENT.md` and `CLAUDE.md`: an invariant stating the three deletion answers — sibling root, fork
  pin, branch-side refusal — and that a branch attaches under the parent's name.
- `ARCHITECTURE.md`: the matrix cell at each phase, and the spike results in *Verified engine
  behaviour*.
- `POSTGRES-AND-STORAGE.md`: `BranchRoot`, the fifth root, once. This document links; it does not
  restate.
- `OPERATIONS.md`: the pin appears in expiry dry-runs; branch storage is bounded by branch count and
  expiry; deleting a branch is the reclaim.
- `EXIT-PATH.md`: a branch is ejected like a catalog; a parent's eject excludes its branches.
- `MCP.md`, `UI.md`, `PUBLIC-API.md`: the tools, the surfaces, the routes, at the phase that ships
  them.
- `LAKEBASE-PLAN.md` phase 6 and `ENTERPRISE-DATA-PLATFORM-ROADMAP.md`'s branching line point here.
- `COMPETITIVE-RESEARCH.md`: re-gather rather than amend when the RFC's status changes.

## Test and acceptance matrix

| Area | Required evidence |
|---|---|
| Zero-copy | Forking a catalog with N Parquet files creates zero objects under either prefix; elapsed time is independent of row count at a fixed file count across two orders of magnitude. |
| Integrity | Deleting one inherited object makes the listing-based check report it while `count(*)` on the branch still answers; the parent-side dry-run names the branch a proposed expiry would strand. |
| Isolation | After writes on the branch, the parent's per-table counts, snapshot list, and object listing are byte-for-byte what they were. |
| Pin | Spike 3 as a test, both directions. Expiry dry-run names the branch it is clamped for. |
| Refusal | `expire` and `cleanup` on a branch return 409 over HTTP, MCP, and the Workbench; `flush` and `compact` succeed and write under the branch prefix. |
| Disposal | Deleting a branch removes only objects under its prefix; a parent object listing taken before and after is identical. Deletion of a `Broken` branch works. |
| Fork at snapshot | Spike 8 as a test, including the created-after, dropped-before, and column-added cases. |
| Diff | Inserts, deletes, and updates on the branch appear with the right `change_type`; the parent's post-fork commits do not; a `Broken` branch reports the break. |
| Merge | Fast-forward succeeds and parent counts equal branch counts; a moved parent is refused with its head; a schema-changed branch is refused; a stale `expectedParentSnapshotId` is refused; a mid-merge failure rolls back and the parent is unchanged. |
| Authorisation | Reader cannot create or merge; catalog-narrowed token reaches branches; branch-narrowed token cannot reach the parent and lists only the branch; unreachable parent is 404 on every branch route. |
| Credentials | No object-store or PostgreSQL credential in any branch row, response, audit record, or log; the parent session holds no secret for the branch prefix. |
| Concurrency | Parent and branch sessions run concurrently; a merge evicts readers on both; a fork taken during a parent write observes the state before or after it, never between. |
| Storage backends | The whole matrix on local filesystem and MinIO; PostgreSQL and local-file metadata both. The integration tests skip without the service, as the existing ones do, and are run before any change to the fork or the pin. |

## Open questions

- **Whole-table or per-row-id merge.** Spike 6 decides. If row ids are stable, deletes are exact and
  the merge commit is proportional to the change; if not, the merge rewrites each changed table and
  the plan should say so in its size estimate.
- **Default expiry.** Fourteen days is a guess at the length of a review. An agent-minted branch
  arguably wants hours. Configurable per instance in phase 1; revisit with usage.
- **Branch names in the Workbench URL.** A branch attaches under the parent's name for SQL, so the
  Workbench must show which branch a session is on somewhere the user cannot miss. The switcher is
  the proposal; a coloured header is the fallback if testing shows people merging the wrong thing.
- **Listing very large prefixes.** The integrity check is one listing of the parent prefix, so its
  cost is the object count divided by the page size. Whether a multi-million-object listing should
  be paged incrementally and cached between scheduled checks is open; on demand and before merge it
  simply runs.
