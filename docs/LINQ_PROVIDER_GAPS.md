# DuckDB.EFCoreProvider follow-ups for the Workbench LINQ planner

**Status date:** 2 August 2026

**Verified against:** LakeHold's pinned `DuckDB.EFCoreProvider` 1.17.0 package and the public APIs
consumed by `Lakehold.Linq.Compiler`.

LakeHold targets `DuckDB.EFCoreProvider` 1.17.0. Releases 1.16.0 and 1.17.0 resolve the original
command-plan, named-execution, type-inspection, and terminal-aggregate extraction gaps. The LINQ
compiler now consumes provider APIs directly instead of intercepting execution, reconstructing
parameter placeholders, or maintaining a duplicate store-type table.

The provider's public, application-independent contract is documented in its
[query command-plan guide](https://github.com/skuirrels/DuckDB.EFCoreProvider/blob/main/docs/QUERY-COMMAND-PLANS.md).

## Current gap ledger

| Capability | Status | Owner / next action |
|---|---|---|
| Non-executing query command plans | Closed in provider 1.16.0 | LakeHold consumes the public API |
| Exact named-command replay | Closed in provider 1.16.0 | LakeHold sends SQL and parameters unchanged |
| Count/Any command plans | Closed in provider 1.16.0 | LakeHold consumes the public API |
| LongCount/Min/Max/Sum/Average command plans | Closed in provider 1.17.0 | LakeHold consumes the public API |
| Structured store-type inspection | Closed in provider 1.17.0 | Provider remains the mapping authority |
| Dynamic `STRUCT` model generation | Open in LakeHold | Generate CLR complex types from `ComplexProperty` metadata |
| Additional native EF property mappings | Open in provider | Add provider mappings before LakeHold exposes the types in LINQ |

## Adopted provider capabilities

- `GetDuckDBCommandPlan(...)` extracts an exact, non-executing command for `IQueryable<T>`.
- `GetDuckDBCountCommandPlan(...)` and `GetDuckDBAnyCommandPlan(...)` cover the supported terminal
  operations without opening the scratch database.
- `GetDuckDBLongCountCommandPlan(...)`, `GetDuckDBMinCommandPlan(...)`,
  `GetDuckDBMaxCommandPlan(...)`, `GetDuckDBSumCommandPlan(...)`, and
  `GetDuckDBAverageCommandPlan(...)` cover the remaining Workbench terminal operations.
- `SqlQueryDynamicCommandAsync(...)` replays exact generated SQL with named ADO.NET parameters, so
  DuckDB braces and provider parameter names are no longer rewritten by LakeHold.
- `GetDuckDBStoreTypeMapping(...)` is the authority for scalar, complex-property, raw-reader-only,
  and unsupported type contracts. It also supplies canonical aliases and faceted mappings.

## Aggregate replay semantics

Provider command plans describe database commands and parameters, not EF's client-side result
shaper. Replaying an aggregate plan therefore exposes DuckDB's database value for an empty sequence
rather than applying EF's client-side empty-sequence behavior. This is the correct contract for the
Workbench's tabular result surface and requires no LakeHold workaround.

## Remaining provider enhancements — broader model mappings

Structured inspection reports `STRUCT` as `ComplexProperty`, but dynamically generating the CLR
complex type and its field mapping remains planner work rather than a provider workaround. In
provider 1.17.0, `MAP`, `UNION`, `VARIANT`, native `ENUM`, fixed-size arrays, `HUGEINT`, `UHUGEINT`,
`BIT`, and `INTERVAL` are classified as raw-reader-only. `VARINT` and unsupported collection shapes
remain outside the scalar EF-property surface without being promoted to an EF model mapping. Those
columns are omitted from generated LINQ row models while supported columns remain queryable.

LakeHold regression coverage now exercises provider mappings for booleans and numeric scalars,
faceted decimals, strings/JSON, UUID, BLOB, DATE, TIME, TIMESTAMP, TIMESTAMPTZ, TIMESTAMP_NS, and
one-dimensional numeric/string arrays. No local store-type map or value converter is used.

The next useful LakeHold enhancement is dynamic `STRUCT` complex-type generation using the
provider's `ComplexProperty` contract. New scalar mappings for the remaining native types belong in
the provider first; LakeHold must continue to consume `GetDuckDBStoreTypeMapping(...)` rather than
guess a CLR type or maintain a second store-type table.

## Not provider work

Compiling catalog schemas into temporary CLR types, sandboxing authored C#, editor diagnostics,
schema fingerprinting, planner discovery, and allowing only a single read-only expression remain
LakeHold responsibilities. Full LINQPad-style multi-statement C# still requires a stronger sandbox
and remains intentionally outside this component.
