# Lakehold

**An open-source lakehouse platform built on DuckDB and DuckLake, with a .NET backend and an
Angular frontend.**

[**lakehold.dev**](https://lakehold.dev)

> Your lakehouse, your bucket, your VPC. A serverless-feeling DuckDB warehouse that runs on your
> own infrastructure, stores every byte as open Parquet you can read without us, and speaks .NET
> natively.

Lakehold is the self-hostable answer to MotherDuck. It provides a multi-tenant query service, a
catalog, and a web SQL IDE over [DuckLake](https://ducklake.select) — an open table format that
stores tables as ordinary Parquet files and metadata as ordinary SQL.

---

## Why

| | MotherDuck | Lakehold |
|---|---|---|
| Deployment | Hosted only | **Self-hosted: laptop, VM, k8s, air-gapped** |
| Storage | Managed; BYO bucket on paid tiers | **Your bucket, always** |
| Format | DuckLake | DuckLake (same) |
| .NET / EF Core | — | **First-class, one model for app and lake** |
| Time travel | ✅ | ✅ |
| CDC / change feeds | Limited | **`ducklake_table_changes`** |
| Maintenance controls | Automatic, hidden | **Explicit: flush, compact, expire, cleanup — dry-run by default** |
| Elastic scale-out | ✅ | Bounded by node size |
| Zero operations | ✅ | You run it |
| Dual execution | ✅ | Not replicated |

The trade is deliberate: **elasticity and zero-ops for control, openness, and .NET integration.**
Full analysis, including where MotherDuck is the better choice, in
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

---

## Quick start

Requirements: .NET 10 SDK, Node 20+.

```bash
# Everything, orchestrated, with the Aspire dashboard
aspire run --project src/Lakehold.AppHost
```

Or run the two halves separately:

```bash
# Terminal 1 — API on :5200, seeds a demo catalog on first run
dotnet run --project src/Lakehold.Api

# Terminal 2 — UI on :5399, proxies /api to the API on :5200
cd web/lakehold-ui && npm start
```

Open <http://localhost:5399>. The first run seeds a `demo` workspace with an `analytics` catalog
containing 250,000 events and 5,000 customers, so the workbench is immediately usable.

---

## Architecture

Two planes, split by whether the workload has a model:

```
Angular SQL IDE  ──REST──▶  Lakehold.Api
                                 │
                 ┌───────────────┴───────────────┐
                 ▼                               ▼
         CONTROL PLANE                     DATA PLANE
    ControlPlaneContext                  LakeContext
    EF model · migrations                model-less · dynamic SQL
    tenants · catalogs · history         Duckling sessions · user SQL
                 └──── DuckDB.EFCoreProvider ────┘
```

A **Duckling** is one tenant's compute session: an in-memory DuckDB instance with that tenant's
DuckLake catalog attached, under a memory limit and thread budget. Isolation is structural — a
tenant can only reference the catalog attached to its own session, so cross-tenant access is
prevented by attachment scope rather than by inspecting submitted SQL.

Both planes run on the same provider, split by whether they have a model rather than by
dependency. The data plane is a model-less `DbContext` serving arbitrary SQL through the provider's
streaming dynamic-query API — see [`docs/PROVIDER-FEEDBACK.md`](docs/PROVIDER-FEEDBACK.md) for how
that changed between provider 1.12.0 and 1.13.0.

| Project | Role |
|---|---|
| `Lakehold.Engine` | Duckling sessions, catalog introspection, maintenance |
| `Lakehold.ControlPlane` | EF Core model: tenants, catalogs, saved queries, audit |
| `Lakehold.Api` | Minimal-API HTTP surface |
| `Lakehold.AppHost` | Aspire orchestration |
| `web/lakehold-ui` | Angular 22 workbench and landing page |

---

## The exit path

The open-format claim is testable, so we test it. After running the demo:

```bash
duckdb -c "SELECT event_type, count(*), sum(revenue)
           FROM read_parquet('src/Lakehold.Api/.lakehold/data/analytics/main/events/*.parquet')
           GROUP BY 1"
```

No DuckLake extension, no Lakehold, no metadata catalog — just Parquet. See
[`docs/EXIT-PATH.md`](docs/EXIT-PATH.md).

One caveat worth knowing: **DuckLake inlines small commits into the metadata catalog rather than
writing Parquet immediately.** A two-row insert produces no data files. Run the **Flush**
maintenance operation (or `ducklake_flush_inlined_data`) to force them out. Lakehold surfaces this
as a first-class control precisely because the guarantee depends on it.

---

## Backup, restore, and scheduling

The metadata catalog is the one part of a DuckLake deployment that is not already an open format, so
Lakehold exports it to Parquet on a schedule and can rebuild a catalog from that export.

```jsonc
{
  "Lakehouse": {
    // A sibling of the data root, never a child: DuckLake's orphan cleanup sweeps everything under
    // the data path that the catalog does not reference, and a backup is by definition unreferenced.
    "BackupRoot": "./.lakehold/backups",
    "BackupRetainCount": 7
  },
  "Lakehold": {
    "Maintenance": {
      "Enabled": true,
      "FlushCron": "0 0/15 * * * ?",   // bounds permanently unrecoverable data
      "BackupCron": "0 0 * * * ?",
      "CompactCron": "0 30 2 * * ?",   // I/O heavy, so off-peak
      "NodeId": "",                    // defaults to the machine name
      "LeaseDuration": "00:30:00"
    }
  }
}
```

Worth knowing:

- **Only non-destructive operations are scheduled.** `expire` and `cleanup` stay manual and
  dry-run-by-default. Automating an irreversible deletion would undo the safety the rest of the
  product argues for.
- **Restore never overwrites.** It writes a new metadata file and refuses if the target exists.
  Re-pointing a tenant at the result is a separate, deliberate step.
- **A backup with no manifest is refused.** If an export died partway and the missing table is
  `ducklake_delete_file`, deleted rows silently return on restore.
- **PostgreSQL metadata restores into a DuckDB file**, so this is an exit path from the catalog
  database and not just a copy of it.
- **Object-store backup roots cannot be pruned.** DuckDB has no delete for object stores, so set a
  lifecycle rule on the prefix. The backup says so rather than reporting "0 pruned".
- **Multi-node deployments take a lease per job per catalog**, so every node firing the same cron
  does not run the same sweep. It engages only for PostgreSQL-backed catalogs — a local file cannot
  be opened by two nodes anyway.

### Tests

```bash
dotnet test Lakehold.slnx     # integration tests skip unless their service is configured
```

The backup tests run against real services rather than mocks, because the failures they guard
against — object stores having no directories, PostgreSQL attaching nothing queryable behind the
catalog — are invisible to the type system:

```bash
docker run -d -e POSTGRES_PASSWORD=lakehold -e POSTGRES_USER=lakehold \
  -e POSTGRES_DB=lakeholdmeta -p 55439:5432 postgres:17
export LAKEHOLD_TEST_POSTGRES="dbname=lakeholdmeta host=localhost port=55439 user=lakehold password=lakehold"

docker run -d -p 59000:9000 -e MINIO_ROOT_USER=lakehold \
  -e MINIO_ROOT_PASSWORD=lakehold123 minio/minio server /data
export LAKEHOLD_TEST_S3_ENDPOINT=http://localhost:59000 LAKEHOLD_TEST_S3_KEY=lakehold \
       LAKEHOLD_TEST_S3_SECRET=lakehold123 LAKEHOLD_TEST_S3_BUCKET=lakehold-test
```

---

## Status

Working today: SQL IDE with catalog explorer and result grid, query history and audit, snapshot
listing for time travel, maintenance operations (flush, compact, expire, cleanup — destructive ones
dry-run by default, with explicit confirmation), scheduled maintenance with multi-node leasing,
catalog backup and restore for both local-file and PostgreSQL metadata, read-only cross-catalog
attach, multi-tenant catalogs, demo seeding.

Next: Postgres wire-protocol endpoint (unlocks Tableau, Power BI, Metabase, DBeaver in one stroke),
MCP server for AI agents, read-only share links.

Not planned: dual local/cloud execution.

---

## Licence

Apache-2.0. Built on [DuckDB](https://duckdb.org), [DuckLake](https://ducklake.select), and
[DuckDB.EFCoreProvider](https://github.com/skuirrels/DuckDB.EFCoreProvider).

[lakehold.dev](https://lakehold.dev)
