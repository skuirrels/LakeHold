# Feedback for DuckDB.EFCoreProvider

Findings from building Lakehold on
[`DuckDB.EFCoreProvider`](https://github.com/skuirrels/DuckDB.EFCoreProvider). Everything below was
reproduced against the shipped NuGet package, not inferred from documentation.

**Status: 1.13.0 closed seven of the eight gaps raised against 1.12.0. The eighth is withdrawn —
it was a bad recommendation.** Lakehold's data plane has moved off raw `DuckDB.NET` and onto the
provider as a result.

---

## Verification against 1.13.0

| # | Gap (raised against 1.12.0) | Status | Verified by |
|---|---|---|---|
| 1 | No untyped result path | ✅ Closed | `SqlQueryDynamicRawAsync` serves every query in Lakehold |
| 2 | No DuckLake maintenance API | ✅ Closed | `Database.DuckLake()` drives all four maintenance operations |
| 3 | Time travel not expressible in LINQ | ✅ Closed | `AsOfSnapshot` / `AsOfTimestamp` present, table- and catalog-scoped |
| 4 | One DuckLake catalog per context | ✅ Closed | `AlsoAttach(...)` wired into `CatalogDescriptor` |
| 5 | DuckLake scaffolding missing | ✅ Closed | Local-metadata scaffolding with catalog filtering |
| 6 | Nested/wide type contract undocumented | ✅ Closed | Nested values preserved; mapping contracts documented |
| 7 | `UseAutoIncrement` namespace | 🚫 **Withdrawn** | Breaking change; the friction is load-bearing (see below) |
| 8 | No read-scaling guidance | ⚠️ Unverified | Documentation item; not checkable from the assembly |

### What the closures actually delivered

**Gap 1 — dynamic queries.** The implementation is better than what was asked for:

```csharp
await using var result = await context.Database.SqlQueryDynamicRawAsync(sql, ct);
foreach (var column in result.Columns) { /* Ordinal, Name, DuckDBTypeName, ClrType */ }
await foreach (var row in result.ReadRowsAsync(ct)) { /* ReadOnlyMemory<object?> */ }
```

Three details worth calling out:

- **It genuinely streams.** `ReadRowsAsync` is an `IAsyncEnumerable`, so Lakehold's row ceiling
  stops the scan rather than truncating an already-materialised list.
- **`ClrType` alongside `DuckDBTypeName`.** Surfacing both sides of the mapping removed a latent
  bug: the result grid previously right-aligned columns by regex over DuckDB type names, which also
  matched `INTERVAL` and would have matched `STRUCT(int)`. It now tests an exact CLR type set.
- **Nested values survive.** `LIST`, `STRUCT`, and `MAP` arrive as CLR collections carrying values,
  and `HUGEINT` round-trips through `BigInteger` with full precision — verified with
  `9223372036854775807`, exact.

**Gap 2 — maintenance.** `Database.DuckLake()` returns a typed facade covering snapshots, expiry,
cleanup, orphan deletion, inline flush, adjacent-file merge, and deleted-row rewrite. Two design
choices are better than proposed:

- **Destructive operations take `dryRun`, defaulting to DuckLake's dry-run mode.** Lakehold now
  exposes this directly: **Expire** and **Cleanup** report what they would remove and commit
  nothing until the operator confirms. That safety affordance exists *because* the provider made
  dry-run a first-class parameter — with hand-built `CALL` statements it would have been extra work
  nobody would have done.
- **Typed results** (`DuckLakeFileRewriteResult.FilesProcessed` / `FilesCreated`,
  `DuckLakeFlushResult.RowsFlushed`) mean the UI reports "merged 12 files into 3" instead of echoing
  an opaque string.

**Gap 6 — type contract.** Documenting the EF entity-property and raw-reader mappings as *separate*
contracts is the right call. Conflating them is exactly the mistake a consumer makes on day one.

---

## Withdrawn

### Gap 7 — `UseAutoIncrement` namespace: **do not move it**

Originally raised as "the cheapest remaining win." That was wrong on two counts, and the proposed
mitigation did not work. Recorded here rather than deleted, because the reasoning is the useful part.

**Why the move is breaking.**

- *Binary.* Moving the containing type changes its assembly-qualified name. Every already-compiled
  consumer binding to
  `DuckDB.EFCoreProvider.Extensions.DuckDBPropertyBuilderExtensions.UseAutoIncrement` fails at load
  with `MissingMethodException`. No shim in the same assembly prevents this; `[TypeForwardedTo]`
  moves types *between assemblies*, not between namespaces.
- *Source.* This is where the original proposal was technically wrong. `[EditorBrowsable(Never)]`
  affects IntelliSense display only — it does **not** remove a method from overload resolution. With
  the extension method present in both `Microsoft.EntityFrameworkCore` and
  `DuckDB.EFCoreProvider.Extensions`, any file importing both gets:

  ```
  CS0121: The call is ambiguous between
          Microsoft.EntityFrameworkCore.…UseAutoIncrement(PropertyBuilder<int>) and
          DuckDB.EFCoreProvider.Extensions.…UseAutoIncrement(PropertyBuilder<int>)
  ```

  Importing both is the provider's own documented pattern, so the "compatibility shim" would break
  precisely the consumers it was meant to protect, and turn a discoverability papercut into a
  compile error.

**Why the friction is load-bearing anyway.** The two backends differ in whether this API means
anything at all:

| Backend | `UseAutoIncrement()` |
|---|---|
| Native DuckDB | Supported, sequence-backed |
| DuckLake | Rejected at model validation — no sequences, no `RETURNING` |

Verified on 1.13.0, the DuckLake rejection is exactly right:

> `DuckLake does not support auto-increment or sequence-backed values. Property 'Row.Id' must use a
> client-assigned value.`

So `UseAutoIncrement` is not a general provider capability — it is a **native-DuckDB-only** one.
Promoting it to `Microsoft.EntityFrameworkCore` would surface it *more* prominently to DuckLake
consumers, who must never call it and instead need `ValueGeneratedNever()` or an explicit
`HasValueGenerator(...)`. Requiring an explicit `using DuckDB.EFCoreProvider.Extensions;` marks it
as provider-specific surface, which is information the conventional placement would erase.

**And the papercut is smaller than reported.** The original complaint was sharpened by building via
`dotnet build` rather than an IDE. Roslyn's add-using code fix resolves extension methods from
referenced assemblies, so in Visual Studio or Rider the CS1061 comes with a one-keystroke remedy.

**Conclusion: keep the current namespace.** The only residual suggestion is cosmetic — the DuckLake
rejection message says "must use a client-assigned value" without naming the API, whereas the
excellent migrations message names `Database.EnsureCreated()` explicitly. Naming
`ValueGeneratedNever()` or `HasValueGenerator(...)` there would make the two consistent.

---

## Outstanding

### Gap 8 — read-scaling guidance

Cannot be verified from the assembly. The ask stands: `DUCKLAKE.md` documents single-writer but not
that *readers* scale out by attaching the same catalog read-only, so the natural reading is still
"DuckDB does not scale for reads," which is untrue for DuckLake. Lakehold continues to serialise
per tenant session and would relax that given a documented pattern.

---

## New observations from 1.13.0

Minor, found while migrating. None are blocking.

1. **No thread-count setter.** `DuckDBDbContextOptionsBuilder` exposes `MemoryLimit(...)` but no
   `Threads(...)`, so Lakehold sets `SET threads` through `ConfigureConnection`. Given memory is
   configurable, threads is the obvious companion — both are per-session resource limits and a
   multi-tenant host wants to bound both.

2. **`AlsoAttach` is local-metadata only.** The signature takes a `metadataPath`, so a PostgreSQL-
   backed share cannot be attached alongside a primary catalog. Sensible as a first cut, but
   multi-tenant deployments that use PostgreSQL metadata for HA are exactly the ones most likely to
   want shares.

3. **No affected-row count on the dynamic path.** `DuckDBDynamicQueryResult` exposes columns and
   rows but no `RecordsAffected`, so DML through the dynamic API reports "no rows returned" rather
   than "N rows affected". A nullable `RecordsAffected` would let an IDE report DML honestly.

4. **`DuckLakeSnapshot.Changes` is typed as `IReadOnlyDictionary<string, IReadOnlyList<string>>`** —
   a real improvement over the raw MAP, which serialised as a type name. Worth noting that
   `CommitMessage` is null for provider-initiated commits; `ducklake_set_commit_message` exists but
   has no facade method. A `WithCommitMessage(...)` on the write path would make the snapshot list
   self-documenting, which is most of the value of having one.

5. **`UseDuckLake(metadataPath, ...)` rejects any non-file metadata path.** Passing a PostgreSQL
   target throws `ArgumentException: DuckLake local metadata sources must be file paths`, pointing
   at "a named-secret profile on a caller-initialized connection". That is the right design — it is
   what keeps the credential out of the options object — but it is only discoverable by hitting the
   exception, because `UseLocalMetadata` is the only metadata method whose name appears in
   IntelliSense next to a path parameter.

   Two small changes would remove the trap entirely:

   - **Name the alternative in the exception.** The message says *what* is wrong and gestures at the
     shape of the fix, but does not say `UseNamedSecret`. Naming the method turns a search through
     the docs into a one-line change.
   - **Document the secret shape.** `UseNamedSecret` says "Reads the metadata and data-storage
     configuration from a named DuckDB secret", but not that the secret must be `TYPE ducklake` with
     `METADATA_PATH`, `DATA_PATH`, and `METADATA_PARAMETERS`. Working that out took a round trip
     through the DuckLake extension's own documentation. One example in the XML doc would cover it:

     ```sql
     CREATE SECRET my_profile (
         TYPE ducklake,
         METADATA_PATH '',
         DATA_PATH 's3://bucket/lake/',
         METADATA_PARAMETERS MAP{'TYPE': 'postgres', 'SECRET': 'my_pg_credentials'});
     ```

   Worth stressing that the underlying behaviour is correct and Lakehold now depends on it: catalog
   records name secrets, secrets are created on the session's connection, and no password reaches a
   descriptor, an options object, or a log. Only the discoverability needs work.

---

## The architectural finding, revised

The original conclusion was:

> The provider is excellent for known-schema persistence and structurally wrong for unknown-schema
> serving.

**The second half is no longer true.** With `SqlQueryDynamicRawAsync`, the provider serves
unknown-schema workloads properly — streaming, with full type metadata, without a CLR type per
result shape. Lakehold's data plane is now a model-less `DbContext` over `UseDuckLake`, and roughly
150 lines of hand-rolled connection lifecycle, type normalisation, and `CALL` string-building were
deleted.

What remains true is the narrower claim: **EF Core's change tracker, model cache, and LINQ pipeline
are not on the path for arbitrary SQL — and 1.13.0 lets you skip them without leaving the provider.**
That is the right place for the boundary, and it is worth stating in the provider's own README,
because "can I serve analytics queries through this?" is the first question an adopter in this space
will ask. As of 1.13.0 the answer is yes.

The remaining split in Lakehold is now about *models*, not dependencies:

- **Control plane** — an EF model, migrations, change tracking. `ControlPlaneContext`.
- **Data plane** — no model, dynamic SQL, streaming. `LakeContext`.

One dependency, one type-mapping implementation, two usage patterns. That is the outcome worth
having.
