# DuckDB.EFCoreProvider documentation

A practical guide to native DuckDB persistence, DuckLake catalogs, high-throughput writes,
open-format analytics, and managed hot-to-cold data lifecycles — for EF Core 10 on .NET 10.

This page documents provider **1.17.1**.

This is the provider LakeHold's own data plane runs on. What LakeHold's engine exercises in
production is documented here as the public contract.

---

## Overview

DuckDB.EFCoreProvider is a complete EF Core 10 relational provider for applications that need
embedded analytics, local persistence, open-format data access, or a shared DuckLake catalog.

| Capability | What it covers |
| --- | --- |
| Application persistence | LINQ, tracking, `SaveChanges`, transactions, migrations, scaffolding, generated keys, and optimistic concurrency. |
| High-speed ingestion | Appender-backed `BulkInsert`, primary-key `Upsert`, plus opt-in batching for tracked writes. |
| Open-format analytics | Query Parquet, CSV, and JSON as mapped entities. Export translated, parameterised LINQ directly to Parquet. |
| Ad-hoc analytics | Stream dynamic SQL results with runtime DuckDB/CLR column metadata and stable nested values. |
| Query tooling | Capture non-executing LINQ command plans, replay exact named parameters, and inspect store-type support without private EF hooks. |
| Lakehouse operations | Query DuckLake history, inspect snapshots, add commit metadata, run typed maintenance, attach local or named-secret reference catalogs, and scaffold local metadata. |
| Managed data lifecycle | Archive relational aggregates to partitioned Parquet, reconcile changes, restore selections, and compact immutable generations. |
| Observability | Use EF Core logging and diagnostics for bounded provider-owned bulk, export, tier, extension, and attachment operations. |

> **Know the engine boundary.** DuckDB is an embedded, single-writer analytical engine. Use it for
> reporting, ETL, local stores, edge workloads, and Parquet-backed analytics — not as a
> high-concurrency, multi-instance OLTP system of record.

---

## Install

### 1. Add the provider

The same package contains native DuckDB and the DuckLake backend profile.

```bash
dotnet add package DuckDB.EFCoreProvider
```

### 2. Register a context

Point EF Core at a persisted DuckDB file. Use `:memory:` only when you explicitly keep the
underlying connection open.

```csharp
builder.Services.AddDbContext<AnalyticsContext>(options =>
    options.UseDuckDB("Data Source=analytics.duckdb"));
```

### 3. Create or migrate, then query

Use normal EF Core lifecycle APIs and compose LINQ against your `DbSet` properties.

```csharp
await db.Database.MigrateAsync();

var totals = await db.Orders
    .AsNoTracking()
    .GroupBy(order => order.Region)
    .Select(group => new {
        Region = group.Key,
        Revenue = group.Sum(order => order.Total)
    })
    .ToListAsync();
```

---

## Choose a backend

Both backends ship in the same package. The choice is about who owns the schema and write
coordination.

| | Native DuckDB file | DuckLake catalog |
| --- | --- | --- |
| Use when | Your application owns the database. | Your application joins a shared data layer. |
| Configure with | `options.UseDuckDB("Data Source=app.duckdb")` | `options.UseDuckLake("catalog/app.ducklake")` |
| LINQ, CRUD, transactions | Yes | Yes |
| Bulk insert and upsert | Yes | Yes (MERGE-based) |
| Migrations | Yes | No |
| Database-first scaffolding | Yes | Local metadata catalogs only |
| Generated values and constraints | Yes | No — application owns logical integrity |
| Tracked-write batching | Yes | No |
| Tiered Parquet archives | Yes | No |

---

## Configure

```csharp
protected override void OnConfiguring(DbContextOptionsBuilder options)
    => options.UseDuckDB(
        "Data Source=analytics.duckdb",
        duckdb => duckdb
            .EnableBulkInsertBatching()
            .EnableBulkUpdateBatching()
            .MemoryLimit("4GB")
            .Threads(4)
            .FileSearchPath("/data,/data/archive")
            .LoadExtension("httpfs"));
```

| Option | Use it for | Default |
| --- | --- | --- |
| `EnableBulkInsertBatching()` | Merging eligible tracked inserts into multi-row SQL. | Off |
| `EnableBulkUpdateBatching()` | Set-based tracked updates without concurrency tokens or computed columns. | Off |
| `EnableBulkDeleteBatching()` | Set-based deletion for eligible tracked entities. | Off |
| `MemoryLimit("4GB")` | Capping DuckDB buffer-manager memory. | DuckDB: 80% RAM |
| `Threads(4)` | Bounding DuckDB parallel-query worker threads. | DuckDB default |
| `FileSearchPath("/data")` | Resolving relative Parquet, CSV, and JSON paths. | DuckDB default |
| `MigrationLockTimeout(…)` | Bounding the wait for the provider migrations lock. | 5 minutes |
| `LoadExtension("httpfs")` | Provisioning remote-file and object-store support. | None |
| `ConfigureConnection(…)` | Creating secrets or applying connection-owned setup. | None |
| `EnableMigrationTableRebuilds()` | Opting into table rebuilds for unsupported in-place constraint changes. | Off |
| `UseNetTopologySuite()` | Loading DuckDB spatial support and mapping NTS geometry operations. | Off |

> **Extension provisioning is deliberate.** The provider supports install-and-load,
> load-only/preinstalled, and caller-managed extension modes. Production images can avoid runtime
> downloads while still declaring what the provider depends on.

`MemoryLimit` and `Threads` configure DuckDB database-instance settings when a connection opens.
Every connection to the same database should use compatible values; they are resource limits, not
per-query isolation controls.

---

## Model and query

### Relational model

Keys, relationships, concurrency tokens, JSON, arrays, lists, temporal values, GUIDs, binary values,
and optional spatial mappings are supported.

```csharp
modelBuilder.Entity<Invoice>(entity =>
{
    entity.HasKey(invoice => invoice.Id);
    entity.Property(invoice => invoice.Id)
        .UseAutoIncrement();
    entity.Property(invoice => invoice.Reference)
        .IsConcurrencyToken();
    entity.HasMany(invoice => invoice.Lines)
        .WithOne(line => line.Invoice);
});
```

### Analytical LINQ

Compose joins, grouping, aggregates, ordering, paging, string/math/temporal functions, arrays, JSON
traversal, and set operations.

```csharp
var revenue = await db.InvoiceLines
    .AsNoTracking()
    .Where(line => line.Invoice.IssuedOn >= start)
    .GroupBy(line => line.Invoice.Region)
    .Select(group => new
    {
        Region = group.Key,
        Revenue = group.Sum(line =>
            line.Quantity * line.UnitPrice)
    })
    .OrderByDescending(row => row.Revenue)
    .ToListAsync();
```

---

## Query command plans

Provider 1.17.0 exposes an application-independent compiler/tooling boundary. It translates a
single-command `IQueryable<T>` or supported terminal operator without opening the connection, and
returns the exact DuckDB command text plus immutable parameter metadata and values.

```csharp
var query = db.Invoices
    .Where(invoice => invoice.IssuedOn >= start)
    .Select(invoice => new { invoice.Region, invoice.Total });

var plan = db.Database.GetDuckDBCommandPlan(query);

await using var result = await db.Database.SqlQueryDynamicCommandAsync(
    plan,
    cancellationToken);
```

Dedicated extractors cover `Count`, `LongCount`, `Any`, `Min`, `Max`, `Sum`, and `Average`.
Predicates and selectors are composed before extraction; numeric `Sum` and `Average` use a supported
numeric or nullable numeric projection. Split/multi-command queries fail explicitly. A replayed
aggregate exposes DuckDB's database value without EF's client-side empty-sequence result shaping.

`SqlQueryDynamicCommandAsync(sql, parameters, ...)` also executes existing named ADO.NET commands
without rewriting SQL braces or mutating caller-owned parameters. Dynamic schema tools can call
`GetDuckDBStoreTypeMapping(storeType)` to distinguish scalar EF properties, `STRUCT` complex
properties, raw-reader-only types, and unsupported types instead of maintaining a duplicate map.

These APIs translate and replay; they do not sandbox authored code or authorize a query. A hosting
tool still owns its source policy, schema/model generation, tenant credentials, read-only SQL
validation, execution limits, and audit. LakeHold's optional isolated C# LINQ Workbench planner is a
real consumer of this contract. See the
[full provider guide](https://github.com/skuirrels/DuckDB.EFCoreProvider/blob/main/docs/QUERY-COMMAND-PLANS.md)
and the [LakeHold integration guide](/docs/linq-workbench).

---

## Write paths

Three write paths trade EF semantics against raw throughput. Choose semantics first, then speed.

| Path | Kind | Use it for | Best at |
| --- | --- | --- | --- |
| `SaveChanges` | Tracked | Change tracking, interceptors, store-generated values, relationship fix-up, and optimistic concurrency. | Application writes |
| `BulkInsert` | Appender | ETL and large ingestion batches. Bypasses tracking and generated values. | New-row throughput |
| `Upsert` | Set-based | Insert new rows and update existing rows by primary key, via a staged appender batch and a set-based merge. | Synchronisation |

```csharp
// Tracked domain write
db.Invoices.Add(invoice);
await db.SaveChangesAsync();

// Raw ingestion fast path
await db.BulkInsertAsync(importedRows);

// Primary-key insert or update
await db.UpsertAsync(synchronisedRows);
```

---

## File analytics

Parquet (columnar analytics), CSV (with schema auto-detection), JSON (document ingestion), and
remote URLs via `httpfs` or Azure can all be mapped and queried directly.

### Query files as entities

```csharp
[FromParquet("archive/invoices/*.parquet")]
public sealed class InvoiceSnapshot
{
    public int Id { get; set; }
    public string Region { get; set; } = "";
    public decimal Total { get; set; }
}
```

### Export translated queries

```csharp
await context.Database.ExportToParquetAsync(
    context.Events.Where(row => row.Timestamp < cutoff),
    "/data/archive/events",
    options => options
        .PartitionBy(row => row.Category)
        .OverwriteOrIgnore()
        .Compression(DuckDBParquetCompression.Zstd),
    cancellationToken);
```

> **Object storage is a connection concern.** Load the required DuckDB extension and create secrets
> through `ConfigureConnection`. The provider supports S3-compatible paths, GCS, R2, and
> Azure-backed archive URLs while keeping credentials outside models and generated SQL.

---

## Dynamic SQL results

Use the dynamic result API when analytical SQL is known at runtime but its columns are not. The
provider opens the reader, exposes ordered DuckDB and CLR metadata, and streams row-owned values
without buffering the complete result set.

```csharp
await using var result = await context.Database
    .SqlQueryDynamicRawAsync(sql, cancellationToken);

foreach (var column in result.Columns)
{
    Console.WriteLine(
        $"{column.Name}: {column.DuckDBTypeName} / {column.ClrType.Name}");
}

await foreach (var row in result.ReadRowsAsync(cancellationToken))
{
    // ReadOnlyMemory<object?>, aligned to Columns by ordinal.
}
```

| API | Use it for |
| --- | --- |
| `SqlQueryDynamicRawAsync(sql)` | Trusted SQL text. A second overload accepts `{0}` placeholders and a parameter list for values. |
| `SqlQueryDynamicAsync($"…{value}…")` | Interpolated SQL whose values become parameters. |
| `ReadRowsAsync()` | Forward-only asynchronous streaming; each returned row owns its backing array. |

The dynamic API is a row-result stream. DuckDB.NET currently reports `RecordsAffected == -1` on its
reader path, so it cannot honestly infer a DML count. When the SQL is known to be DML and the
affected-row count matters, use `ExecuteSqlRawAsync` instead.

> **Raw SQL remains trusted SQL.** Parameterize values, but never splice untrusted identifiers or
> SQL fragments into raw text. The provider normalizes database nulls and preserves DuckDB.NET
> nested values; it deliberately does not choose a UI row limit or JSON serialization policy.

See also: [the entity and raw-reader type-mapping contracts](https://github.com/skuirrels/DuckDB.EFCoreProvider/blob/main/docs/TYPE-MAPPINGS.md).

---

## Types and spatial

EF entity properties and raw DuckDB.NET reader values are separate contracts. Model mappings follow
configured EF types; dynamic SQL reports and returns the driver's runtime values without forcing
them through an entity mapping.

| Area | Representative mappings |
| --- | --- |
| EF scalar | `Guid → UUID`, `DateOnly → DATE`, `DateTime → TIMESTAMP`, `DateTimeOffset → TIMESTAMPTZ`. |
| EF structured | `JsonDocument`/`JsonElement → JSON`; one-dimensional arrays and lists map to DuckDB lists. |
| Raw reader | `HUGEINT → BigInteger`, `STRUCT → Dictionary<string, object>`, `MAP → Dictionary<K,V>`. |
| Spatial add-on | NetTopologySuite geometries map to DuckDB `GEOMETRY` with translated spatial members, methods, and aggregates. |

### Enable NetTopologySuite

```bash
dotnet add package DuckDB.EFCoreProvider.NTS
```

```csharp
options.UseDuckDB(
    "Data Source=spatial.duckdb",
    duckdb => duckdb.UseNetTopologySuite());
```

The add-on loads DuckDB's spatial extension. Geometry is stored in the native `GEOMETRY` type; WKT
is only the current driver wire representation for reading and writing values.

See also: [complete type mappings and precision notes](https://github.com/skuirrels/DuckDB.EFCoreProvider/blob/main/docs/TYPE-MAPPINGS.md).

---

## DuckLake

The profile loads the DuckLake extension, runs secret initialisation, safely attaches the catalog,
and selects it before EF uses provider-owned or caller-owned connections.

```csharp
options.UseDuckLake(
    duckLake => duckLake
        .UseNamedSecret("application_lake")
        .CatalogName("analytics")
        .CreateIfNotExists(false),
    duckDB => duckDB
        .LoadExtension("httpfs")
        .ConfigureConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = BuildCreateSecretFromEnvironment();
            command.ExecuteNonQuery();
        }));
```

The named secret must be a DuckDB `TYPE ducklake` secret. Create it on each session connection so
credentials remain outside descriptors, options, and logs:

```sql
CREATE SECRET application_lake (
    TYPE ducklake,
    METADATA_PATH '',
    DATA_PATH 's3://bucket/lake/',
    METADATA_PARAMETERS MAP {
        'TYPE': 'postgres',
        'SECRET': 'application_postgres'
    });
```

| Supported | Different contract |
| --- | --- |
| LINQ and relational raw SQL | No physical PK/FK/unique enforcement |
| Tracked insert, update, and delete | No store-generated values or sequences |
| Transactions and concurrency checks | No EF migrations or `EnsureDeleted` |
| Initial `EnsureCreated` | No tracked-write batching |
| `BulkInsert` and MERGE `Upsert` | No provider tiered storage |
| Historical LINQ and typed maintenance | Application owns lifecycle policy and authorization |
| Read-only, named-secret, and additional local or named-secret catalogs | Additional catalogs are raw/dynamic-SQL targets, not EF entity targets |
| Local-metadata database-first scaffolding | Generated entities are keyless until logical keys are reviewed |

> **DuckLake reads scale through independent sessions.** Create a separate read-only `DbContext` or
> provider-owned read-only connection for each concurrent operation and attach the same catalog. The
> metadata backend must support those concurrent clients, and a `DbContext` must never be shared
> across threads. This is client scale-out, not a DuckDB replica topology.

### Historical queries and maintenance

Use a separate catalog-wide profile when joins and navigations must observe one coherent historical
snapshot. The profile is forced read-only and disables creation and automatic metadata migration.

```csharp
var historicalOptions = new DbContextOptionsBuilder<AnalyticsContext>()
    .UseDuckLake(
        "metadata.ducklake",
        lake => lake.AsOfSnapshot(snapshotId))
    .Options;

await using var historical = new AnalyticsContext(historicalOptions);
var rows = await historical.Events
    .Where(row => row.RecordedAt < cutoff)
    .ToListAsync();

// For one deliberately pinned query root:
var tableHistory = await context.Events
    .AsOfTimestamp(asOf)
    .ToListAsync();
```

`Database.DuckLake()` provides catalog-scoped typed maintenance. Snapshot expiry, old-file cleanup,
and orphan-file deletion default to discovery-only dry runs; pass `dryRun: false` only after
reviewing the returned candidates.

```csharp
var lake = context.Database.DuckLake();
var snapshots = await lake.GetSnapshotsAsync(cancellationToken);
var expiryCandidates = await lake.ExpireSnapshotsAsync(
    cutoff,
    cancellationToken: cancellationToken); // dry run

await lake.FlushInlinedDataAsync(
    new DuckLakeFlushOptions { SchemaName = "main", TableName = "events" },
    cancellationToken);
await lake.MergeAdjacentFilesAsync(
    new DuckLakeMergeOptions { TableName = "events" },
    cancellationToken);
```

Make a provider-initiated write snapshot self-documenting by setting caller-owned metadata inside
the same explicit transaction as the writes:

```csharp
await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
await context.SaveChangesAsync(cancellationToken);
await context.Database.DuckLake().SetCommitMessageAsync(
    author,
    commitMessage,
    extraInfo,
    cancellationToken);
await transaction.CommitAsync(cancellationToken);
```

### Additional catalogs and scaffolding

`AlsoAttach` adds a verified local catalog and `AlsoAttachNamedSecret` adds a catalog whose metadata
and storage configuration live in a caller-created `TYPE ducklake` secret. Both are read-only by
default. EF entities continue to target the selected primary catalog; use catalog-qualified dynamic
or raw SQL for additional catalogs.

```csharp
lake.CatalogName("analytics")
    .AlsoAttach("reference", "reference.ducklake")
    .AlsoAttachNamedSecret("shared", "shared_profile");
```

```bash
dotnet ef dbcontext scaffold \
  "ducklake:/absolute/path/metadata.ducklake" \
  DuckDB.EFCoreProvider
```

Local-metadata scaffolding filters by the exact catalog and honors normal `--schema`/`--table`
filters. DuckLake exposes no physical keys, so generated entities are keyless until you review them
and declare logical keys. Remote and named-secret metadata require a caller-initialized connection
so credentials never enter command-line arguments.

See also: [the production DuckLake guide](https://github.com/skuirrels/DuckDB.EFCoreProvider/blob/main/docs/DUCKLAKE.md).

---

## Tiered storage

Tiered storage splits an aggregate across two tiers. Hot data stays in writable DuckDB tables with
normal tracking, `SaveChanges`, and navigations. Cold data moves to immutable partitioned Parquet on
local disk or object storage, organised by declared Hive partition transforms. `ArchiveTierAsync`
performs the transition, which is verified and crash-safe. Generated union views keep ordinary LINQ
queries spanning both tiers.

```csharp
modelBuilder
    .ToTieredStore<Record>(
        record => record.EffectiveAt,
        "/var/data/archive/records",
        TierGranularity.Month)
    .MatchBy(record => record.ExternalKey)
    .PartitionBy(partitions => partitions
        .By(record => record.GroupId, "root_group_id")
        .ByMonth(record => record.EffectiveAt))
    .WithReadModel<RecordReport>()
    .Including<RecordPart>(
        record => record.Parts,
        part => part.WithReadModel<RecordPartReport>());
```

> **Read the views without duplicate CLR types.** `WithTieredView()` requests the same
> provider-managed hot/Parquet union view without registering a second read-model type, and
> `ToTieredView(…)` maps your existing entity types in a separate read-only context. Partition
> pruning is contract-checked in both, so independently deployed readers fail explicitly on layout
> drift instead of silently filtering valid history. One physical child table can sit beneath
> multiple independently archived roots, and every partition level can carry an explicit Hive
> directory name.

### Operations

| Operation | API | What it does |
| --- | --- | --- |
| Archive | `ArchiveTierAsync` | Move complete aggregate windows to Parquet and advance the published watermark. |
| Bootstrap | `BootstrapArchiveTierAsync` | Publish a first half-open archive window with an explicit, aligned lower bound while older rows remain hot. |
| Reconcile | `ReconcileArchiveTierAsync` | Publish approved late rows, corrections, or explicit tombstones into a new immutable generation. |
| Restore | `RestoreArchiveTierAsync` | Bring an exact key or partition selection back into mapped hot tables without duplication. |
| Compact | `CompactArchiveTierAsync` | Rewrite the active cold data into a verified full generation with bounded manifest evidence. |
| Retain | `PlanArchiveRetentionAsync` / `PublishArchiveRetentionAsync` | Review and publish an immutable replacement generation while keeping the prior generation for rollback. |
| Inspect | `GetArchiveGenerationInventoryAsync` | Read inventory and preflight evidence, page conflicts or detached descendants, and plan/revalidate safe cleanup. |
| Recover | `PlanArchiveRecoveryAsync` / `ApplyArchiveRecoveryAsync` | Rebuild provider control, exact catalog, and views from a previously persisted checkpoint without mutating Parquet. |
| Evolve | `RewriteArchiveContractAsync` | Inspect archive contracts and explicitly rewrite incompatible column layouts with verified mappings. |

### Reviewable retention

```csharp
var plan = await db.Database.PlanArchiveRetentionAsync<Record>(
    new TierArchiveRetentionOptions
    {
        RetainFrom = retainFromUtc,
        RetainedPartitionScopes =
        [
            TierMaintenanceScope.ForPartitionValues(
                new Dictionary<string, object?>
                {
                    [nameof(Record.GroupId)] = retainedGroupId,
                }),
        ],
    });

// Review the fingerprint, boundary, counts, files, and scopes.
var publication = await db.Database
    .PublishArchiveRetentionAsync<Record>(plan);
```

Planning fingerprints the active generation, exact physical catalog, contracts, boundary, partition
scopes, and per-node counts. Publication revalidates that evidence, writes and verifies a
replacement, then atomically switches the generated views. The provider does not decide retention
policy, legal holds, approval, rollback depth, or deletion of obsolete local/remote objects.

### Control-state recovery

```csharp
// Capture after a successful publication and store outside the DuckDB file.
TierArchiveRecoveryCheckpoint checkpoint = await db.Database
    .CaptureArchiveRecoveryCheckpointAsync<Record>();

// After local control loss: plan first, then apply the reviewed plan.
TierArchiveRecoveryPlan recovery = await db.Database
    .PlanArchiveRecoveryAsync<Record>(persistedCheckpoint);
TierArchiveGenerationInventory inventory = await db.Database
    .ApplyArchiveRecoveryAsync<Record>(recovery);
```

The checkpoint carries binding, active generation, watermark, contracts, counts, and exact-file
fingerprints but no archive path. Planning is read-only; apply revalidates and atomically rebuilds
provider metadata and views. It never creates, modifies, adopts by guesswork, or deletes Parquet
objects.

> **Evidence is explicit.** Property filters on tiered views become Hive-bucket predicates; ordered,
> paged, and keyset queries are verified with DuckDB `EXPLAIN` file counts. Local and S3-compatible
> MinIO lanes are always on. Real AWS, GCS, and Azure matrices are credential-gated and opt-in; if a
> cloud lane is skipped, that environment is reported as unverified rather than counted as passing
> evidence.

See also: [the complete tiered-storage contract](https://github.com/skuirrels/DuckDB.EFCoreProvider/blob/main/docs/TIERED-STORAGE.md)
and the [compatibility and release-acceptance matrix](https://github.com/skuirrels/DuckDB.EFCoreProvider/blob/main/docs/TIERED-STORAGE-COMPATIBILITY.md).

---

## Performance

The figures below are developer-laptop measurements from the repository guide, over 100,000 rows.
Treat them as order-of-magnitude guidance and run BenchmarkDotNet on your own hardware.

| Write path | 100,000 rows |
| --- | --- |
| `SaveChanges` | ~12.5 seconds |
| `BulkInsert` | ~90 milliseconds |

### Choosing a path by batch size

| Size | Guidance |
| --- | --- |
| 1–5 rows | Prefer `SaveChanges` when you need full EF semantics. The paths are near break-even at the smallest sizes. |
| 10+ rows | `BulkInsert` already pays off when tracking, generated values, and interceptors are unnecessary. |
| Hundreds–millions | Use one appender call when memory allows. Chunk only to bound source memory, not because DuckDB needs it. |
| Analytical reads | Use `AsNoTracking`, project only required columns, and let DuckDB's vectorised engine do the grouping and filtering. |

See also: [benchmark method and full caveats](https://github.com/skuirrels/DuckDB.EFCoreProvider/blob/main/docs/PERFORMANCE.md).

---

## Schema lifecycle

### Native DuckDB

Create tables, columns, indexes, sequences, comments, and migration history through normal EF
migrations.

```bash
dotnet ef migrations add InitialCreate
dotnet ef database update
```

### DuckLake

Use `EnsureCreated` for initial provisioning, then apply reviewed DuckLake schema-evolution SQL
through a controlled deployment process.

```csharp
await db.Database.EnsureCreatedAsync();
```

> **DuckDB cannot perform every ALTER TABLE operation in place.** The provider fails clearly for
> unsupported constraint changes by default. `EnableMigrationTableRebuilds()` opts into
> target-model table rebuilds where the provider can perform them safely.

See also: [native migration behaviour and limitations](https://github.com/skuirrels/DuckDB.EFCoreProvider/blob/main/docs/MIGRATIONS.md).

---

## Diagnostics and logging

Normal queries, tracked writes, connections, transactions, and migrations use EF Core's standard
logging, diagnostic, and interception surfaces. Provider-owned raw operations publish one bounded
start/completion/failure lifecycle through the same pipeline, without requiring a provider-specific
logger.

```csharp
using DuckDB.EFCoreProvider.Diagnostics;
using Microsoft.Extensions.Logging;

options.UseDuckDB("Data Source=analytics.duckdb")
    .LogTo(
        Console.WriteLine,
        new[]
        {
            DuckDBEventId.BulkInsertCompleted,
            DuckDBEventId.TieredStorageOperationFailed,
        },
        LogLevel.Information);
```

| Event family | Coverage |
| --- | --- |
| Bulk insert and upsert | The complete raw operation, including duration and affected rows on successful completion. |
| Parquet export | Start, completion, and failure around the translated export command. |
| Tiered storage | Archive, bootstrap, reconciliation, restore, retention, recovery, contract rewrite, compaction, and purge lifecycles. |
| Infrastructure | Configured extension loading and DuckLake catalog attachment. |

For structured consumption, use the `LogTo` overload that receives EF `EventData` or subscribe to
`DiagnosticListener`, then cast provider events to `DuckDBOperationEventData`. The payload exposes
the operation, non-secret target, duration, optional affected-row count, exception, and `DbContext`.

> **Raw fast paths stay outside DbCommandInterceptor.** `BulkInsert`, `Upsert`, and provider
> maintenance use lower-level DuckDB operations. Their public observability contract is the bounded
> lifecycle event, not every internal staging or verification statement.

---

## Compatibility

| Workload | Fit | Why |
| --- | --- | --- |
| Reporting and dashboards | **Strong** | Columnar reads, analytical LINQ, local deployment. |
| Embedded and edge stores | **Good** | In-process database with normal EF persistence. |
| ETL and Parquet analytics | **Strong** | Appender ingestion and direct file queries. |
| Shared lakehouse analytics | **Good** | DuckLake profile when the app owns logical integrity. |
| High-concurrency OLTP | **No** | DuckDB is embedded and single-writer. |
| Full DuckDB SQL surface via LINQ | **Mixed** | PIVOT, ASOF, QUALIFY, and other constructs need raw SQL. |

---

## Reference

- [README](https://github.com/skuirrels/DuckDB.EFCoreProvider/blob/main/README.md) — complete setup, models, workflows, troubleshooting, and examples.
- [Capability map](https://github.com/skuirrels/DuckDB.EFCoreProvider/blob/main/docs/CAPABILITY-MAP.md) — supported areas, engine limitations, provider gaps, and roadmap.
- [DuckLake guide](https://github.com/skuirrels/DuckDB.EFCoreProvider/blob/main/docs/DUCKLAKE.md) — secrets, catalog lifecycle, model rules, and production boundaries.
- [Tiered storage](https://github.com/skuirrels/DuckDB.EFCoreProvider/blob/main/docs/TIERED-STORAGE.md) — archive invariants, partitioning, reconciliation, restore, and operations.
- [Type mappings](https://github.com/skuirrels/DuckDB.EFCoreProvider/blob/main/docs/TYPE-MAPPINGS.md) — EF entity mappings, raw DuckDB.NET result values, JSON, nesting, and precision.
- [Migrations](https://github.com/skuirrels/DuckDB.EFCoreProvider/blob/main/docs/MIGRATIONS.md) — native DuckDB schema lifecycle, limitations, locking, and opt-in table rebuilds.
- [Performance](https://github.com/skuirrels/DuckDB.EFCoreProvider/blob/main/docs/PERFORMANCE.md) — benchmark commands, indicative results, caveats, and write-path guidance.
- [Runnable sample](https://github.com/skuirrels/DuckDB.EFCoreProvider/blob/main/samples/Quickstart/README.md) — create a database, write tracked data, bulk insert, upsert, and query.
- [DuckLake sample](https://github.com/skuirrels/DuckDB.EFCoreProvider/blob/main/samples/DuckLake/README.md) and [tiered-storage sample](https://github.com/skuirrels/DuckDB.EFCoreProvider/tree/main/samples/TieredStorage) — runnable storage-profile examples.

Install from [NuGet](https://www.nuget.org/packages/DuckDB.EFCoreProvider) or browse the source on
[GitHub](https://github.com/skuirrels/DuckDB.EFCoreProvider). Community provider, MIT licensed, and
not affiliated with DuckDB Labs or Microsoft.
