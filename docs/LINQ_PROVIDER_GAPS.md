# DuckDB.EFCoreProvider follow-ups for the Workbench LINQ planner

LakeHold targets `DuckDB.EFCoreProvider` 1.17.0. Releases 1.16.0 and 1.17.0 resolve the original
command-plan, named-execution, type-inspection, and terminal-aggregate extraction gaps. The LINQ
compiler now consumes provider APIs directly instead of intercepting execution, reconstructing
parameter placeholders, or maintaining a duplicate store-type table.

The provider's public, application-independent contract is documented in its
[query command-plan guide](https://github.com/skuirrels/DuckDB.EFCoreProvider/blob/main/docs/QUERY-COMMAND-PLANS.md).

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

## Remaining provider enhancement — broader model mappings

Structured inspection reports `STRUCT` as `ComplexProperty`, but dynamically generating the CLR
complex type and its field mapping remains planner work rather than a provider workaround. `MAP`, fixed-size arrays, `HUGEINT`,
`UHUGEINT`, `VARINT`, `BIT`, and `INTERVAL` remain raw-reader-only rather than scalar EF properties.
Those columns are omitted from generated LINQ row models while supported columns remain queryable.

LakeHold regression coverage now exercises provider mappings for booleans and numeric scalars,
faceted decimals, strings/JSON, UUID, BLOB, DATE, TIME, TIMESTAMP, TIMESTAMPTZ, TIMESTAMP_NS, and
one-dimensional numeric/string arrays. No local store-type map or value converter is used.

Broader EF mappings would make more catalog columns directly expressible in Workbench LINQ. The
provider should continue to own the CLR contract and translation behavior; LakeHold should only
generate a model from that public contract.

## Not provider work

Compiling catalog schemas into temporary CLR types, sandboxing authored C#, editor diagnostics,
schema fingerprinting, planner discovery, and allowing only a single read-only expression remain
LakeHold responsibilities. Full LINQPad-style multi-statement C# still requires a stronger sandbox
and remains intentionally outside this component.
