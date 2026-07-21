# Performance benchmarks — before / after

Reproducible before/after measurements for the performance changes. Each benchmark runs the
**old** and **new** implementations side by side in one process, so the numbers are comparable on
whatever machine runs them.

## Running

```bash
# Backend (#1–#4). Release build matters — Debug numbers are meaningless.
dotnet run -c Release --project benchmarks/Lakehold.Benchmarks

# A subset:
dotnet run -c Release --project benchmarks/Lakehold.Benchmarks -- wire sweep

# Frontend filter (#5):
node benchmarks/filter-bench.mjs
```

The project is intentionally **self-contained** — no references to the product projects. Each
before/after arm reimplements the exact code under test, so the suite builds and runs regardless of
the rest of the repo's working-tree state.

## Methodology

- Warm up first, then time several trials and take the **median** to shed outliers.
- Allocation is read exactly from `GC.GetAllocatedBytesForCurrentThread()` (workstation GC), not
  sampled. It is the headline metric for the CPU-bound changes.
- `#1` and `#4` are I/O-bound (a real DuckDB round trip); their absolute times are
  machine/provider-dependent, so read the **ratio and the shape**, not the nanoseconds.
- Not a substitute for BenchmarkDotNet: no process isolation, no per-invocation overhead modelling.
  Good enough to compare two implementations of the same operation; not for publishing absolutes.

## What each measures

| # | Benchmark | Old → new |
|---|-----------|-----------|
| 1 | `resolve` | Per-query control-plane read (`AsNoTracking` + tenant join on native DuckDB) → cache hit |
| 2 | `sweep`   | Idle-sweep run on every `GetOrStartAsync` (list + scan of N sessions) → throttled O(1) skip |
| 3 | `wire`    | `ToWireValue` stringifying every integer → in-range integers stay JSON numbers (box reused) |
| 4 | `parquet` | Backup row count via `count(*)` re-scan → `parquet_file_metadata` footer read |
| 5 | filter    | Catalog-explorer filter lowercasing every keystroke → precomputed lowercase index |
| 6 | —         | Result-grid `@let align` is a template-compile change; no runtime workload to sample |

## Snapshot (Apple-class laptop, 14 cores, .NET 10.0.8, DuckDB 1.5.3, Node 24)

Indicative only — rerun locally for your hardware.

| # | Metric | Before | After | Result |
|---|--------|-------:|------:|--------|
| 1 | ns / resolve | ~4.4 ms | ~31 ns | control-plane round trip eliminated per query |
| 2 | bytes / call | 1,568 B | 0 B | sweep allocation removed on the hot path (~1.2× faster) |
| 3 | per result row | 264 B | 192 B | 27% fewer bytes/row, ~1.16× faster (2 BIGINTs no longer stringified) |
| 4 | µs / table (5M rows) | ~297 µs | ~139 µs | ~2.1× faster; independent of row count |
| 5 | ms / keystroke (wide catalog) | 0.09–1.21 ms | 0.06–0.52 ms | 1.6–2.7× faster per keystroke |

`#1`'s ratio is large because the old path opened a DuckDB connection against the control-plane file
on **every** query; the cache replaces that with an in-memory lookup. The absolute "before" is
provider/connection dependent — the takeaway is the round trip is gone, not the specific multiple.
