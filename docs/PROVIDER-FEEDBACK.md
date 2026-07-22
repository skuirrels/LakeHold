# Feedback for DuckDB.EFCoreProvider

Findings from building Lakehold on
[`DuckDB.EFCoreProvider`](https://github.com/skuirrels/DuckDB.EFCoreProvider). Behaviour claims were
reproduced against the shipped NuGet package rather than inferred; the few that rest on the
provider's own documentation say so and quote it, which is the distinction gap 8 below was retracted
for missing.

**Current: Lakehold runs 1.14.0.** Of the twelve numbered items: six closed, two withdrawn on the
evidence by this side, one declined — correctly, with the fix landing here instead — and three still
open. Three further asks (`Threads`, `AlsoAttachNamedSecret`, `SetCommitMessageAsync`) were delivered
in 1.14.0 and are all in use.

The data plane has moved off raw `DuckDB.NET` and onto the provider entirely, and nothing outstanding
blocks a workload. The two capabilities that would have justified keeping a second stack — a truthful
write count and concurrent reads — both turned out to be reachable through the provider's own context
and connection.

The document is ordered by *round*, because the reasoning is the part worth keeping and it only makes
sense in the order it happened. The table below is the shortcut.

### Status of everything raised

| # | Item | Status | Since |
|---|---|---|---|
| 1 | No untyped result path | ✅ Closed | 1.13.0 — `SqlQueryDynamicRawAsync` serves every query in Lakehold |
| 2 | No DuckLake maintenance API | ✅ Closed | 1.13.0 — `Database.DuckLake()` drives all four maintenance operations |
| 3 | Time travel not expressible in LINQ | ✅ Closed | 1.13.0 — `AsOfSnapshot` / `AsOfTimestamp`, table- and catalog-scoped |
| 4 | One DuckLake catalog per context | ✅ Closed | 1.13.0 — `AlsoAttach(...)`; secret-backed shares followed in 1.14.0 |
| 5 | DuckLake scaffolding missing | ✅ Closed | 1.13.0 — local-metadata scaffolding with catalog filtering |
| 6 | Nested/wide type contract undocumented | ✅ Closed | 1.13.0 — nested values preserved; mapping contracts documented |
| 7 | `UseAutoIncrement` namespace | 🚫 **Withdrawn** | Breaking change, and the friction is load-bearing — [why](#gap-7--useautoincrement-namespace-do-not-move-it) |
| 8 | No read-scaling guidance | 🚫 **Retracted** | The guidance was already in the provider's docs — [why](#1-read-scaling--the-gap-was-ours) |
| 9 | No precision, scale, or nullability on columns | ⬜ **Open** | Wire protocol sends `-1` type modifiers — [detail](#gap-9--column-metadata-carries-no-precision-scale-or-nullability) |
| 10 | Cannot learn a result's shape without executing | ⬜ **Open** | Worked around by deferring `Describe` — [detail](#gap-10--no-way-to-learn-a-results-shape-without-executing-it) |
| 11 | No affected-row count on the dynamic path | ⛔ **Declined** | Correctly: the count is DuckDB.NET's to expose — [detail](#4-affected-row-counts--declined-correctly-and-the-workaround-has-a-trap) |
| 12 | Raw SQL is parsed as a format string | ⬜ **Open** | Found while resolving 11 — [detail](#gap-12--raw-sql-is-parsed-as-a-format-string) |

[Round four](#round-four--provider-response-and-what-changed-here) covers the provider's reply to all
of it, including what each 1.14.0 API was measured to do before Lakehold adopted it.
[What is still open](#what-is-still-open) is the short list at the end.

---

## Round one — verification against 1.13.0

The eight gaps in rows 1–8 were raised against 1.12.0 and checked against 1.13.0 as shipped.

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

## Withdrawn by this side

Two items were raised and then withdrawn on the evidence, one per round. Both are kept rather than
deleted: a request that turned out to be wrong is more useful to the next reader than a clean list,
and in both cases the reasoning is the part that transfers.

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

### Gap 8 — read-scaling guidance: **retracted**

Raised as "cannot be verified from the assembly, so the ask stands." That was the mistake: an item
that cannot be checked from the artefact under test should have been checked against the source
documentation before being filed, and the guidance was already there. See
[round four](#1-read-scaling--the-gap-was-ours). What the round did produce is a measurement Lakehold
needed anyway, and a change it may now make.

---

## Round two — observations from migrating to 1.13.0

Minor, found while migrating; none were blocking. **Four of the five are closed in 1.14.0 and in use;
the fifth was declined, correctly.** They are kept in their original form, with the outcome marked,
because a request and its answer are only informative together.

1. ✅ **Closed in 1.14.0.** *No thread-count setter.* `DuckDBDbContextOptionsBuilder` exposes
   `MemoryLimit(...)` but no `Threads(...)`, so Lakehold sets `SET threads` through
   `ConfigureConnection`. Given memory is configurable, threads is the obvious companion — both are
   per-session resource limits and a multi-tenant host wants to bound both.

2. ✅ **Closed in 1.14.0.** *`AlsoAttach` is local-metadata only.* The signature takes a
   `metadataPath`, so a PostgreSQL-backed share cannot be attached alongside a primary catalog.
   Sensible as a first cut, but multi-tenant deployments that use PostgreSQL metadata for HA are
   exactly the ones most likely to want shares.

3. ⛔ **Declined**, and resolved on this side — see
   [item 4 of round four](#4-affected-row-counts--declined-correctly-and-the-workaround-has-a-trap).
   *No affected-row count on the dynamic path.* `DuckDBDynamicQueryResult` exposes columns and
   rows but no `RecordsAffected`, so DML through the dynamic API reports "no rows returned" rather
   than "N rows affected". A nullable `RecordsAffected` would let an IDE report DML honestly.

4. ✅ **Closed in 1.14.0**, in a better form than asked for. *`DuckLakeSnapshot.Changes` is typed as
   `IReadOnlyDictionary<string, IReadOnlyList<string>>`* — a real improvement over the raw MAP, which
   serialised as a type name. Worth noting that
   `CommitMessage` is null for provider-initiated commits; `ducklake_set_commit_message` exists but
   has no facade method. A `WithCommitMessage(...)` on the write path would make the snapshot list
   self-documenting, which is most of the value of having one.

5. ✅ **Closed.** The 1.14.0 IntelliSense XML and packaged README both carry the
   `METADATA_PARAMETERS` example below, so the round trip through DuckLake's own documentation is no
   longer needed. *`UseDuckLake(metadataPath, ...)` rejects any non-file metadata path.* Passing a
   PostgreSQL target throws `ArgumentException: DuckLake local metadata sources must be file paths`,
   pointing at "a named-secret profile on a caller-initialized connection". That is the right
   design — it is what keeps the credential out of the options object — but it is only discoverable
   by hitting the exception, because `UseLocalMetadata` is the only metadata method whose name
   appears in IntelliSense next to a path parameter.

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

## Round three — findings from the wire endpoint

Lakehold now serves the PostgreSQL frontend/backend protocol (`docs/POSTGRES-WIRE.md`), which put a
different kind of load on the dynamic path than the HTTP API does: it needs a result's *shape* before
its rows, a type description precise enough for a client to parse values, and statements arriving
from a driver rather than from a person. All of the following was reproduced against 1.13.0 as
shipped and still holds on 1.14.0.

### Correction — parameterised dynamic queries already exist

Worth stating first because it corrects an assumption made while building, not the provider. The
wire endpoint currently refuses bound parameters, and the initial reasoning was that the dynamic path
had no way to carry them. It does:

```csharp
// Both overloads are public on DuckDBDatabaseFacadeExtensions:
SqlQueryDynamicRawAsync(DatabaseFacade, string, CancellationToken)
SqlQueryDynamicRawAsync(DatabaseFacade, string, IReadOnlyList<object>, CancellationToken)
```

A positional `IReadOnlyList<object>` is exactly the shape the protocol's `Bind` message supplies —
parameters arrive as an ordered list bound to `$1, $2, …` — so this maps across with no impedance.
Refusing parameters is a Lakehold gap and the next thing to implement there, not a provider one.

The only feedback that remains is discoverability: the parameterised overload is the one a consumer
serving arbitrary SQL needs most, and nothing in the type name or the surrounding documentation
suggests it exists. Naming it in whatever documents `SqlQueryDynamicRawAsync` would have saved a
design decision from being made around a limitation that was not there.

### Verified strength — cancellation interrupts the engine, it does not just abandon the await

This mattered enough to measure rather than assume, because the failure mode is invisible until
production: if a token only abandoned the `await`, a runaway BI query would keep a DuckDB thread and
its session gate for as long as it liked, and every other statement for that tenant would queue
behind a query nobody was waiting for any more.

Measured against a scan that would otherwise run for minutes, with a two-second token:

```
OUTCOME=cancelled  ELAPSED_MS=2006
REUSABLE=yes       ROWS=1
```

The scan stops when the token fires, not when it finishes, and the session is immediately usable
afterwards. That is the behaviour a multi-tenant host needs and it is worth documenting explicitly —
"is cancellation real?" is a question every adopter serving user-submitted SQL has to answer, and
right now the only way to answer it is to build the experiment.

### Gap 9 — column metadata carries no precision, scale, or nullability

**Status: open.** Unchanged in 1.14.0; the wire endpoint still sends `-1` type modifiers.

`DuckDBDynamicColumn` exposes `Ordinal`, `Name`, `DuckDBTypeName`, and `ClrType`. For a wire protocol
that is not quite enough: `RowDescription` declares a *type modifier* per column, which for
`DECIMAL(18,4)` is how a client learns the precision and scale, and nullability decides whether a
client can treat a column as a non-nullable value type.

Lakehold sends `-1` for the type modifier, so that information is lost between DuckDB and the BI
tool. The only alternative available is to string-parse `"DECIMAL(18,4)"` out of `DuckDBTypeName` —
which is precisely the regex-over-type-names pattern that gap 1's closure was credited with
eliminating from the result grid. Reintroducing it one layer down would be a poor trade.

A structured descriptor alongside the existing string would close it: logical type id, precision and
scale where they apply, nullability, and child types for `LIST`, `STRUCT`, and `MAP`. That last part
has a second consumer already — mapping nested types onto anything other than "render it as text"
needs the children, and today they are only reachable by inspecting the CLR values that come back.

### Gap 10 — no way to learn a result's shape without executing it

**Status: open, and worked around.** Deferring the `Describe` reply until `Execute` produces columns
is sound and invisible to clients, so this is a design smell rather than a blocker — but the second
half of it, `ParameterTypes`, becomes load-bearing the moment bound parameters are implemented.

The protocol asks for a row description at `Describe`, which arrives *before* `Execute`. The shape of
arbitrary SQL is only knowable by running it, and the two available workarounds are both bad:
answering "no data" makes clients reject the rows that follow, and planning the statement a second
time to discover its shape executes every query twice.

Lakehold's resolution was to defer the `Describe` reply until `Execute` produces columns, which is
sound but is a workaround for a missing capability rather than a design. DuckDB's own C API supports
preparing a statement and reading its result schema without executing it, so the capability exists
underneath:

```csharp
// Sketch of what would close it:
await using var prepared = await context.Database.PrepareDynamicAsync(sql, ct);
IReadOnlyList<DuckDBDynamicColumn> columns = prepared.Columns;      // no rows scanned
IReadOnlyList<Type> parameters = prepared.ParameterTypes;           // for ParameterDescription
await using var result = await prepared.ExecuteAsync(values, ct);
```

The `ParameterTypes` half matters as much as the columns: the protocol has a `ParameterDescription`
message that is currently answered with "none", which is honest only because parameters are refused.
Once they are supported, that message needs real types and there is nowhere to get them.

### Gap 11 — `RecordsAffected` (reinforcing observation 3 above)

**Status: declined, and resolved on this side.** The provider was right to refuse it, the count is
reachable through `ExecuteNonQuery`, and the wire endpoint now completes writes with a real tag. The
reasoning is in
[item 4 of round four](#4-affected-row-counts--declined-correctly-and-the-workaround-has-a-trap);
what remains is an upstream DuckDB.NET ask, not a provider one.

Already recorded from the HTTP path; the wire endpoint hits it independently and harder. Postgres
completes a statement with a tag carrying the affected count — `INSERT 0 12`, `UPDATE 7`,
`DELETE 3` — and clients parse it. With no affected-row count on the dynamic path, Lakehold reports
`INSERT 0 0` for a successful insert of any size.

Two unrelated consumers now want the same nullable `RecordsAffected`, which is usually the signal
that it belongs in the API rather than in each caller.

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
are not on the path for arbitrary SQL — and 1.13.0 onward lets you skip them without leaving the
provider.**
That is the right place for the boundary, and it is worth stating in the provider's own README,
because "can I serve analytics queries through this?" is the first question an adopter in this space
will ask. As of 1.13.0 the answer is yes.

The remaining split in Lakehold is now about *models*, not dependencies:

- **Control plane** — an EF model, migrations, change tracking. `ControlPlaneContext`.
- **Data plane** — no model, dynamic SQL, streaming. `LakeContext`.

One dependency, one type-mapping implementation, two usage patterns. That is the outcome worth
having.

The wire endpoint is the strongest evidence for that conclusion so far, because it is a consumer the
dynamic path was never designed for: a database protocol, serving a driver rather than a person,
streaming results with no ceiling straight to a socket. It needed no second stack and no return to
raw `DuckDB.NET` — the three gaps above are refinements of an API that already carried the workload,
not blockers against it. An answer of "yes, you can serve analytics traffic through this" now covers
BI tools as well as browsers.

---

## Round four — provider response, and what changed here

The provider replied to rounds one to three: it accepted the generic-API and discoverability items,
added three APIs in 1.14.0, declined one, and corrected one of this document's claims. Everything
below was measured against the shipped package rather than taken on either side's word, because half
the point of the exercise is that assertions about a dependency should be reproducible.

**1.14.0 shipped, and Lakehold is on it.** All three accepted APIs are present and all three are now
in use, each verified against the shipped package before adoption:

| API | Measured | Where it lands |
|---|---|---|
| `Threads(int)` | Configured session reports `threads = 3`, an unconfigured one `14` | Replaced the `SET threads` hack in `Duckling.StartAsync` |
| `AlsoAttachNamedSecret(...)` | Cross-catalog read returns 3; a write is refused by the engine — *"Cannot execute statement of type INSERT on database "share" which is attached in read-only mode"* | `AttachedCatalog` gained a metadata kind; secret-backed shares now attach |
| `SetCommitMessageAsync(...)` | Snapshot 4 carries `maintenance: flush`; called outside a transaction it throws, as documented | Flush and compaction commit labelled snapshots |

One behaviour was worth confirming before adopting the third: a maintenance run that changes nothing
adds no snapshot (`before=5 afterNoOpFlush=5 afterCompact=5`), so labelling does not turn a scheduled
sweep into a stream of empty history entries.

### 1. Read scaling — the gap was ours

> The current 1.13 source documentation already explicitly described scaling reads with separate
> read-only `DbContext`/connection instances.

Accepted, and gap 8 is retracted above. The item was filed on the strength of what could not be seen
in the assembly, which is not evidence of absence in a *documentation* item. The guidance is in the
1.14.0 packaged README, so the failure mode is gone for the next adopter — but the process error was
on this side.

The permission that came with it is the useful part:

> Lakehold may relax its read gate where each concurrent operation owns a separate context and
> connection and the metadata backend supports concurrent readers.

Measured, on local-file metadata — the backend least likely to tolerate it, and the one this
deployment defaults to. Four read-only contexts, each with its own connection, scanning a 300,000-row
table concurrently:

```
C: reader 0: ok (42858)    C: reader 2: ok (42857)
C: reader 1: ok (42857)    C: reader 3: ok (42857)
```

And again with a separate read-write context committing throughout:

```
D: reader 0..2: ok         D: writer: 20 commits ok
```

No reader failed, no reader blocked, and the writer's commits did not disturb them. **Invariant 5's
gate is therefore a Lakehold choice rather than a provider constraint.**

One correction to that measurement, though, from the 1.14.0 README itself:

> PostgreSQL metadata supports multiple local or remote clients, while a DuckDB metadata file is a
> single-client profile.

So the local-file run above worked *outside* the documented contract rather than because of it, and
it should not be read as a licence to build on. A read pool must be gated on the metadata backend:
PostgreSQL metadata is where the provider says concurrent clients are supported, and that is the
configuration to relax first. The permission stands, but with the provider's boundary rather than
this test's.

Relaxing it is a per-tenant read pool: N read-only contexts per session, each with its own memory
budget, plus read/write routing and eviction. That is a change with its own design and its own tests,
so it is recorded here as the next step rather than smuggled into this pass. What has changed is that
it is now known to be available, which it was not before.

### 2. Threads — the caveat is right, and does not bind this topology

> DuckDB treats this as database-instance configuration, not a genuinely independent per-session
> tenant limit. A shared in-process database instance therefore cannot provide different thread
> ceilings for different tenants.

Correct as stated, and worth knowing. It reads as a warning that Lakehold's per-tenant thread limit
may be an illusion, so it was worth measuring whether a Duckling is a shared instance. It is not —
two contexts on `Data Source=:memory:`:

```
A: context B cannot see context A's table (separate instances)
A: threads in A = 3, in B = 14
```

A distinct DuckDB instance per context, and `SET threads` confined to the one it was set on. A
Duckling is one context and one connection, so its thread ceiling is real and a tenant cannot raise
another's. The caveat binds a host that shares one instance across tenants; this one does not, and
`Threads(...)` has replaced the `ConfigureConnection` call that used to set it — which also removes a
latent hazard: the thread setting and a caller's object-store secret were competing for the same
connection hook.

### 3. Additional attachments — closed

`AlsoAttachNamedSecret(...)` closes observation 2, and Lakehold now uses it: `AttachedCatalog`
carries a metadata kind, exactly as the primary catalog does, so an extra catalog is a local path
or a secret name. Rule 13 holds unchanged for shares — a credential still never reaches a catalog
record, an options object, or a log.

Worth recording that the read-only promise is the provider's, not a convention on this side. A write
through a secret-backed share is refused by the engine, which is what makes invariant 9 testable
rather than aspirational.

### 4. Affected-row counts — declined, correctly, and the workaround has a trap

> The provider cannot truthfully manufacture a value without parsing SQL, interpreting result-column
> names, or executing the command twice. Those would all be incorrect provider behaviour.

Agreed without reservation. A provider that sniffed verbs would be wrong for every consumer that
disagreed with its guesses, and the honest `-1` is better than a plausible fabrication. The right fix
is the named one — `RecordsAffected` on the DuckDB.NET reader — and there is a datapoint for it:
**`ExecuteNonQuery` on the same connection already returns the count.**

```
B: INSERT 3 -> 3 affected    B: UPDATE 2 -> 2    B: DELETE 1 -> 1
```

So the number exists one layer below the reader that reports `-1`. That is a smaller upstream ask
than it looked: not a new capability, a surfaced one.

**The recommended workaround does not survive arbitrary SQL, though.** `ExecuteSqlRawAsync` parses
its statement as a composite format string, so a brace is read as a placeholder:

```
INSERT INTO s VALUES (1, {'a': 1, 'b': 'x'}, MAP {'k': 7})
→ FormatException: Input string was not in a correct format.
  Failure to parse near offset 26. Expected an ASCII digit.
```

That is an ordinary DuckDB struct and map literal, and it never reaches the engine. Doubling the
braces does work — but only while EF keeps formatting, and the day it stops, the doubled braces
become part of the statement and corrupt it silently. So for a consumer serving *user-authored* SQL
the guidance needs one more clause: run it as a non-query on a caller-owned command, not through
`ExecuteSqlRawAsync`. Filed as gap 12 below.

**What Lakehold did.** A statement whose leading keyword is `INSERT`, `UPDATE`, `DELETE`, or `MERGE`,
and which carries no `RETURNING`, now executes as a non-query on the session's own connection and
reports what it changed (`StatementVerb`, `Duckling.ExecuteNonQueryUnguardedAsync`). The wire
endpoint completes with `INSERT 0 3` rather than `INSERT 0 0`, and the workbench says "3 rows
affected" rather than "0 rows".

The classification is deliberately kept where it belongs. Lakehold is a SQL front end and already
owes its clients a command tag naming the verb, so the keyword is something it must know anyway; a
provider owes nobody that. It is a reporting choice and not a security one — isolation is still which
catalog is attached (invariant 4) — and a statement it fails to recognise, such as a CTE-led write,
streams exactly as before and reports no count. Losing a number is recoverable; losing a result set
is not, which is why `RETURNING` is excluded rather than assumed away.

### 5. Commit messages — closed, and the ambient version was rightly refused

> Commit metadata belongs to a specific active transaction and should not leak into later writes.

That is a better answer than the request. `WithCommitMessage(...)` as ambient state would attach the
last message set to whatever wrote next, which on a multi-tenant host with a shared maintenance path
is a snapshot list that is worse than the empty one — confidently mislabelled rather than blank.
Lakehold's use for `SetCommitMessageAsync(...)` is maintenance, and it is now wired up: flush and
compaction run inside a transaction whose snapshot is labelled `lakehold maintenance: …`, so the
history distinguishes what the platform did from what the tenant did. Backup is not labelled, because
it exports rather than commits, and expiry and cleanup remove snapshots rather than adding one to
name.

The transaction requirement is the design working. `SetCommitMessageAsync` outside a transaction
throws — *"requires an active transaction so the metadata is attached to one DuckLake snapshot"* —
which is the failure the ambient version would have made silent.

### 6. Metadata-path discoverability — closed

The exception now names `UseNamedSecret(...)`, and the documented `TYPE ducklake` example carries the
profile shape. The clarification that the shown fields describe that profile rather than every
DuckLake secret is worth keeping in the doc, since the example is the thing an adopter will copy.

### Gap 12 — raw SQL is parsed as a format string

**Status: open.** 1.14.0 adds no non-query on the dynamic path, which is the shape a fix would take.

Not a DuckLake or DuckDB issue and not, strictly, the provider's own behaviour — it is EF's raw-SQL
path — but it lands on any provider consumer that forwards user-authored SQL, and it is invisible
until a struct literal shows up in production. The measurement is in item 4 above.

Two things would defuse it:

- **Say so where the workaround is recommended.** Any documentation that points a consumer at
  `ExecuteSqlRawAsync` for DML counts should say the statement is format-parsed and is therefore
  unsuitable for SQL the consumer did not author.
- **Offer a non-query on the dynamic path.** `SqlQueryDynamicRawAsync` is documented as the place
  arbitrary SQL goes, and it treats the text as SQL rather than as a format string. An
  `ExecuteDynamicRawAsync` returning the engine's own affected count would put writes on the same
  footing as reads, use `ExecuteNonQuery` under the hood, and require no verb sniffing anywhere —
  the caller chooses the method, which is exactly the choice a provider is entitled to make the
  caller make.

### Where this leaves the dependency

Nothing in this round changed the architectural conclusion, and one thing strengthened it. The two
capabilities that would have justified a second stack — a truthful write count and concurrent reads —
turned out to be reachable through the provider's own context and connection. The count needed one
method the provider deliberately does not wrap, and the reads need only separate contexts, which is
what its documentation said all along.

---

## What is still open

Three provider-side items, none blocking, in the order they would pay off:

1. **Gap 12 — a non-query on the dynamic path.** The smallest of the three and the one with a
   consumer waiting: it would let Lakehold delete its verb classification entirely, because the
   caller would choose the method instead of the code guessing from a keyword.
2. **Gap 9 — structured column metadata.** Precision, scale, nullability, and child types. Without
   it a wire protocol either lies about `DECIMAL(18,4)` or string-parses a type name, and nested
   types stay renderable but not mappable.
3. **Gap 10 — prepare without executing.** Its `ParameterTypes` half turns from a design smell into
   a blocker the moment bound parameters land in the wire endpoint, because
   `ParameterDescription` then needs real types and there is nowhere to get them.

One upstream, in DuckDB.NET rather than the provider: **`RecordsAffected` on the data reader.** The
number already exists — `ExecuteNonQuery` returns it — so this is surfacing a value, not inventing
one, and it would close gap 11 properly rather than by workaround.

And two on this side, recorded here because both were unblocked by answers in round four rather than
by anything Lakehold discovered on its own:

- **A per-tenant read pool**, on PostgreSQL metadata first. Reads scale with a context and connection
  each, but the provider documents a DuckDB metadata file as single-client, so the local-file
  measurement above is not the licence it looks like. Invariant 5's session gate is now a Lakehold
  choice, and replacing it is a design with its own tests rather than a flag to flip.
- **Bound parameters in the wire endpoint.** The parameterised dynamic overload has been there all
  along; refusing `Bind` values is this side's gap, and it is what gap 10's `ParameterTypes` half is
  waiting on.
