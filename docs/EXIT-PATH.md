# The exit path

A lock-in claim is only credible if it is tested. This document records how to leave Lakehold and
what we verified, so the "open format" claim in the README is falsifiable rather than decorative.

## What is on disk

A Lakehold catalog is two things, both open:

| Component | Format | Readable by |
|---|---|---|
| Table data | Plain Apache Parquet | DuckDB, Spark, Polars, DataFusion, pandas, Trino, … |
| Catalog metadata | Ordinary SQL tables (DuckDB file or PostgreSQL) | Any SQL client |

There is no proprietary container, no encrypted manifest, and no service call required to
interpret either half.

## Layout

DuckLake writes data files under `<data_path>/<schema>/<table>/`:

```
.lakehold/data/analytics/
└── main/
    ├── events/    ducklake-019f7c9f-957d-….parquet
    └── customers/ ducklake-019f7c9f-95c1-….parquet
```

The layout is per-table, so a glob must target one table's directory. A `**/*.parquet` glob across
the whole data path unions files with different schemas by position, which either errors or
silently produces nonsense — verified, and a mistake worth avoiding in your own migration scripts.

## Verified: read the data with no Lakehold in the loop

Against the seeded demo catalog, using plain DuckDB with **no `ducklake` extension loaded**:

```sql
SELECT event_type, count(*) AS n, ROUND(sum(revenue), 2) AS revenue
FROM read_parquet('.lakehold/data/analytics/main/events/*.parquet')
GROUP BY event_type
ORDER BY event_type;
```

Result — all 250,000 rows, fully intact:

```
purchase   62,500 rows   rev=15,625,000.00
refund     62,500 rows   rev=15,625,625.00
signup     62,500 rows   rev=15,624,375.00
view       62,500 rows   rev=15,623,750.00
```

## The one caveat: inlined data

**DuckLake does not write Parquet for every commit.** Small writes are inlined into the metadata
catalog and only materialise as data files once a threshold is crossed.

Measured on DuckDB 1.5.3 + DuckLake:

| Write | Parquet files produced |
|---|---|
| `INSERT` of 2 rows | **0** |
| `INSERT` of 200,000 rows | 1 |

So immediately after a small write, the newest rows exist only in the metadata catalog. They are
not lost and not proprietary — the catalog is still ordinary SQL — but a Parquet-only reader will
not see them.

**Before treating the Parquet as complete, flush:**

```sql
CALL ducklake_flush_inlined_data('analytics');
```

or press **Flush** in the workbench toolbar. Lakehold exposes this as a first-class maintenance
operation rather than hiding it, because the open-format guarantee depends on it. A deployment that
makes the claim publicly should flush on a schedule.

## Disaster recovery: the metadata catalog is lost

A separate question from migration, and a more alarming one. If the metadata database is destroyed
and only the Parquet under `data_path` survives, what can be recovered?

**Short answer: the current table contents, yes — exactly. History, no. And the obvious way to do
it silently returns wrong data.**

### The metadata catalog is not a cache

This is the part the "open Parquet" claim can obscure. DuckLake uses **merge-on-read deletes**: a
`DELETE` does not rewrite the data file, it records the deletion elsewhere. Verified on DuckDB
1.5.3, deleting 10 rows and updating 10 more in a 100,000-row table produced:

```
ducklake-…-b70b….parquet          1,378,041 b   original 100,000 rows, unchanged
ducklake-…-b72b….parquet              1,105 b   the UPDATE's 10 new rows
ducklake-…-b7xx…-delete.parquet       1,421 b   20 delete markers
```

The delete file contains `file_path`, `pos`, and a snapshot id — enough to reconstruct deletions,
**but only if you read it**. Three failure modes follow:

| Naive approach | Result |
|---|---|
| Glob `*.parquet`, ignore delete files | **Deleted rows resurrect.** Our 10 "right to be forgotten" rows came back. |
| Same | **Updated rows duplicate** — `id=25` yields both `e25@x.com` and `redacted`. |
| Metadata lost *before* a flush | **Unflushed updates are gone**, and the rows revert to their pre-update values. |

The first is the dangerous one. If deletions were made for legal reasons, a naive rebuild silently
reinstates personal data you were obliged to erase.

### Verified recovery procedure

Three details have to be right, and each one failed on the first attempt:

1. **Read data files and delete files separately.** Globbing them together with `union_by_name=true`
   merges the delete file's `pos` column into the data rows, so any column you alias `pos` is
   silently renamed `pos_1` and the anti-join matches nothing — returning a wrong answer with no
   error. Without `union_by_name`, the same glob errors out instead.
2. **Use `union_by_name=true` across data files.** They are heterogeneous: files produced by an
   `UPDATE` carry `_ducklake_internal_row_id` and `_ducklake_internal_snapshot_id`; the original
   bulk-insert file does not.
3. **Anti-join on both `file_path` and `pos`.** Matching on path alone discards every row of any
   file that appears in the delete file.

Enumerate the files in your recovery tool rather than relying on glob semantics, then:

```sql
-- Data files: every ducklake-*.parquet that is NOT *-delete.parquet
CREATE TABLE d AS
SELECT *, filename AS _src, file_row_number AS _rn
FROM read_parquet([<data files>], filename=true, file_row_number=true, union_by_name=true);

CREATE TABLE x AS
SELECT file_path, pos FROM read_parquet([<delete files>], union_by_name=true);

CREATE TABLE recovered AS
SELECT d.* EXCLUDE (_src, _rn, file_row_number,
                    _ducklake_internal_snapshot_id, _ducklake_internal_row_id)
FROM d LEFT JOIN x ON x.file_path = d._src AND x.pos = d._rn
WHERE x.file_path IS NULL;
```

Verified against ground truth on the scenario above:

| Check | Recovered | Truth |
|---|---|---|
| Rows | 99,990 | 99,990 |
| `sum(id)` | 4,999,949,955 | 4,999,949,955 |
| Deleted rows present | 0 | 0 |
| `id=25` email | `redacted` | `redacted` |
| Duplicate ids | 0 | 0 |

### What is not recoverable

- **Snapshot history and time travel.** Gone entirely. You recover the latest state, not the past.
- **Inlined data never flushed.** Permanently lost — this is the strongest argument for a flush
  schedule.
- **Liveness of files.** Superseded files left by compaction are indistinguishable from live ones
  without the catalog, so a rebuild can include data that was logically replaced.
- **Views, comments, tags, and column rename history.**

### Better: back the catalog up *into the bucket*, as Parquet

Everything above describes recovering table contents from data files alone. There is a far better
option, and Lakehold ships it: **export the metadata catalog to Parquet next to the data it
describes.** The bucket then holds everything needed to reconstitute the lakehouse, with no separate
backup system involved — which is the point of bring-your-own-bucket.

Press **Backup** in the workbench toolbar, or:

```
POST /api/tenants/{tenant}/catalogs/{catalog}/maintenance/backup
```

It writes every metadata table under `<data path>/_catalog_backup/<UTC timestamp>/`:

```
.lakehold/data/analytics/
├── main/                      3.3 MB   table data
└── _catalog_backup/
    └── 20260720T180738Z/      120 KB   30 metadata tables as Parquet
```

Timestamped, so each run keeps a generation rather than overwriting the last known-good backup.
The export is proportional to the number of files and snapshots, not to row count — on the demo
catalog it is ~3.6% of data size, and that ratio falls as data grows.

### Restoring from a Parquet catalog backup

Verified end to end on DuckDB 1.5.3: the metadata database was deleted, rebuilt from the Parquet
export, and reattached.

```sql
-- 1. Rebuild a metadata database from the backup, one table per Parquet file.
ATTACH 'restored.ducklake' AS r;
CREATE TABLE r.main.ducklake_snapshot   AS SELECT * FROM read_parquet('…/ducklake_snapshot.parquet');
CREATE TABLE r.main.ducklake_data_file  AS SELECT * FROM read_parquet('…/ducklake_data_file.parquet');
-- … repeat for all 30 files; the file name is the table name.
DETACH r;

-- 2. Attach it as an ordinary DuckLake catalog against the surviving data.
ATTACH 'ducklake:restored.ducklake' AS lake (DATA_PATH '…/data/analytics');
```

What came back, checked against ground truth:

| Check | Restored | Truth |
|---|---|---|
| Row count | 49,990 | 49,990 |
| Deleted rows still deleted | ✅ 0 | 0 |
| Updated value | `redacted` | `redacted` |
| Snapshots | 8 | 8 |
| Views | ✅ present | present |
| `AT (VERSION => 2)` time travel | ✅ 50,000 rows | 50,000 |

**Full fidelity, including history.** That is a materially better outcome than the row-level
reconstruction above, which recovers current contents but loses every snapshot.

### Therefore: back up the metadata catalog

Treat it as authoritative state, not derived state. It is small — kilobytes to megabytes against
gigabytes of Parquet — so there is no excuse. Use the built-in **Backup** operation, which runs
hourly by default.

### PostgreSQL metadata is an exit path too, not just a backup

Backup covers **both** metadata kinds, and — this is the useful part — a backup taken from
PostgreSQL restores into a plain DuckDB file. The metadata database is the one component of a
DuckLake deployment that is not already an open format, so being able to walk away from it matters
as much as being able to walk away from the query engine.

Verified against PostgreSQL 17 and DuckDB 1.5.3, in
`tests/Lakehold.Engine.Tests/PostgresCatalogBackupTests.cs`:

| Check | Source (PostgreSQL) | Restored (DuckDB file) |
| --- | --- | --- |
| Metadata tables | 30 | 30 |
| Live rows | 2,993 | 2,993 |
| Deleted rows still deleted | 0 | 0 |
| Duplicate ids | 0 | 0 |
| Snapshots | ✅ | equal |

`pg_dump` remains the right tool for point-in-time recovery of the PostgreSQL server itself. It is
not a substitute for this: a dump restores *into PostgreSQL*, whereas the Parquet backup restores
into a file you can open with the `duckdb` CLI and no Lakehold at all.

Two mechanical notes:

- The metadata tables are read through a **read-only** attach, using a named DuckDB secret. Nothing
  in a catalog record or an options object holds the credential.
- The maintenance lease lives in its own `lakehold` schema rather than in `public`, so it cannot
  collide with a DuckLake migration and never rides along into a backup.

### Backups in an object store: read the retention caveat

The backup root can be an `s3://` prefix, and listing and restoring from a bucket both work
(verified against an S3-compatible endpoint). **Retention does not.** DuckDB can read and write
object stores but cannot delete from them, so a remote backup root grows without bound.

The backup reports this rather than passing over it in silence — the result carries
`RetentionDeferred`, and the maintenance detail reads *"retention deferred (object stores need a
storage lifecycle rule)"*. Set an expiry lifecycle rule on the backup prefix in your bucket. A
"0 pruned" that actually meant "cannot prune" is exactly the sort of thing nobody notices until the
bill arrives.

"Your data is open Parquet you can read without us" is true. "Parquet alone is the whole truth of
your table" is not — deletions and liveness live in the catalog, and that distinction is worth
understanding *before* you need it.

## Full migration checklist

1. **Back up the catalog** — the Backup button. This covers PostgreSQL metadata as well as local
   files, and restores either into a plain DuckDB file.
2. **Flush inlined data** — `ducklake_flush_inlined_data`, or the Flush button.
3. **Compact** — `ducklake_merge_adjacent_files`, so you copy few large files rather than many
   small ones.
4. **Copy the data path** — `aws s3 sync`, `rsync`, or your object store's own tooling. These are
   just files.
5. **Export the metadata — not optional.** It is authoritative for deletions and file liveness, not
   just schema history. Without it, deleted rows resurrect on rebuild. Step 1 already covers this
   for both metadata kinds; keep a `pg_dump` too if you want to restore the PostgreSQL server
   itself rather than the catalog it holds.
6. **Verify row counts** per table between the source catalog and the copied Parquet before
   decommissioning anything.

Step 6 is not ceremony. Do it while the old system still runs.
