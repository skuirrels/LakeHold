# Lakehold repository guidance

This file is the durable working context for coding agents in this repository. Keep it aligned with
the live code and with `README.md` and `docs/ARCHITECTURE.md` when the architecture changes.

## Product and stack

Lakehold is a self-hostable, multi-tenant lakehouse built on DuckDB and DuckLake. It deliberately
trades managed elasticity for infrastructure control, open Parquet storage, and first-class .NET
integration.

- Backend: .NET 10, ASP.NET Core minimal APIs, EF Core 10, DuckDB.EFCoreProvider.
- Frontend: Angular 22 with TypeScript 6 and npm.
- Orchestration: .NET Aspire.
- Local durable state: `.lakehold/` below the API project unless configuration overrides it.
- Package versions are managed centrally in `Directory.Packages.props`; project files should not
  add versions to individual `PackageReference` items.

## Repository map

- `src/Lakehold.Engine`: data plane, Duckling sessions, dynamic SQL execution, catalog browsing,
  and DuckLake maintenance.
- `src/Lakehold.ControlPlane`: modelled EF Core state for tenants, catalogs, saved queries, and
  query/audit history.
- `src/Lakehold.Api`: HTTP contracts, minimal-API endpoints, configuration, and demo seeding.
- `src/Lakehold.AppHost`: Aspire composition for the API and Angular development server.
- `src/Lakehold.ServiceDefaults`: health, resilience, service discovery, and telemetry defaults.
- `web/lakehold-ui`: Angular workbench, catalog explorer, result grid, and landing page.
- `docs/ARCHITECTURE.md`: architectural rationale and current product boundaries.
- `docs/EXIT-PATH.md`: verified open-format exit procedure and Parquet caveats.
- `docs/PROVIDER-FEEDBACK.md`: provider capabilities and why the data plane uses its dynamic API.

## Architectural invariants

Preserve these unless the task explicitly changes the architecture and updates its documentation.

1. The control plane and data plane are split by whether the workload has a known model, not by
   dependency. Both use DuckDB.EFCoreProvider.
2. `ControlPlaneContext` is the modelled EF Core context on native DuckDB. It needs features such as
   sequences and `RETURNING` that are not provided by the DuckLake profile.
3. `LakeContext` is intentionally model-less. Arbitrary result shapes must use the provider's
   streaming `SqlQueryDynamicRawAsync` path; do not add fake entity types or reintroduce a parallel
   raw `DuckDBConnection` stack without a demonstrated provider gap.
4. A `Duckling` is the tenant isolation and compute unit. Isolation comes from which catalog is
   attached to a session. Do not treat parsing, filtering, or rewriting submitted SQL as the
   security boundary.
5. A Duckling owns a non-thread-safe `DbContext` and a single-writer DuckDB instance. Query and
   maintenance access must remain serialised through the session gate.
6. Query results are streamed and capped by `LakehouseOptions.MaxRowsPerResult`. Preserve
   cancellation, statement timeouts, and early termination so large results are not fully
   materialised before truncation.
7. Catalog and extension identifiers cannot always be parameterised. Validate and quote them with
   `SqlIdentifier`; parameterise ordinary values wherever the underlying API permits it.
8. Object-store credentials belong in provider connection configuration. Never persist them in a
   catalog, options object, response, source file, or log.
9. Read-only additional catalogs must remain read-only. Do not widen write access to implement
   sharing or cross-catalog queries.
10. Snapshot expiry and old-file cleanup are destructive and must remain dry-run by default with an
    explicit apply/confirmation path. Flush and compaction are non-destructive maintenance.
11. Catalog backups live under `BackupRoot`, a sibling of the data root and never a child of it.
    Anything under the data path that the catalog does not reference is a candidate for DuckLake's
    orphan cleanup, so a nested backup deletes itself once it ages.
12. Restore never overwrites an existing catalog, and never restores a generation with no manifest.
    An interrupted export missing `ducklake_delete_file` would silently reinstate deleted rows.
13. Remote metadata is addressed by DuckDB secret name, never by connection string. The provider
    rejects a non-file metadata path, and the secret is created in connection configuration so no
    credential reaches a catalog record, an options object, or a log.
14. The maintenance lease belongs in the `lakehold` schema, not `public`. It must not collide with a
    DuckLake migration, and it must not be swept into a catalog backup.

## Open-format guarantee

DuckLake may inline small commits in its metadata catalog, so the newest rows are not necessarily
present in Parquet immediately. Before claiming, testing, exporting, or decommissioning based on
the Parquet exit path:

1. Flush inlined data.
2. Compact where appropriate.
3. Copy both the table data and, when history is required, the metadata catalog.
4. Independently compare per-table row counts before removing the source.

Target one table directory at a time when reading raw Parquet. A recursive glob across tables with
different schemas can fail or silently combine columns incorrectly. Keep `docs/EXIT-PATH.md` and
the runtime behavior consistent whenever maintenance or storage semantics change.

## Coding conventions

- Nullable reference types, implicit usings, current language features, code-style enforcement,
  latest recommended analysis, and warnings-as-errors are enabled centrally.
- Follow existing namespaces, file-scoped namespace style, typed minimal-API results, and concise
  XML documentation for public APIs.
- Keep async operations cancellable end to end. Pass request cancellation tokens through EF,
  provider, query, and maintenance calls, and use `ConfigureAwait(false)` consistently with the
  surrounding backend code.
- Preserve structured logging and avoid logging submitted data, credentials, or secret-bearing
  connection details.
- Keep provider-to-CLR conversion in the provider. `Duckling.ToWireValue` is only the JSON wire
  projection, including lossless string transport for integers and decimals beyond JavaScript's
  safe numeric range.
- Keep API DTOs in the API layer and engine/control-plane concerns out of Angular components.
- Follow the existing standalone Angular component and service patterns. Keep API calls in
  `lakehouse.service.ts`, shared wire shapes in `models.ts`, and component-specific styling beside
  the component.
- Update `README.md` or the relevant document when changing public behavior, architectural
  boundaries, provider assumptions, maintenance semantics, or the exit path.

## Local and generated files

Do not edit or commit build output, dependency caches, IDE state, or runtime lakehouse data:

- `bin/`, `obj/`, `dist/`, `node_modules/`, `.angular/`, `.npm-cache/`
- `.idea/`, `.vscode/`, `.DS_Store`
- any `.lakehold/` directory, including catalog databases and Parquet files

Treat existing `.lakehold/` data as user state. Do not delete, reseed, migrate, or overwrite it
unless the user explicitly authorises that operation and the impact is understood.

## Build, run, and verification

Requirements are the .NET 10 SDK and Node.js 20 or newer.

```bash
# Restore and build all backend projects
dotnet build Lakehold.slnx

# Reproducible frontend dependency install, when needed
npm ci --prefix web/lakehold-ui

# Production frontend compilation
npm run build --prefix web/lakehold-ui

# Run the complete local product with Aspire
aspire run --project src/Lakehold.AppHost

# Or run the halves separately: API on :5200, UI on :5399
dotnet run --project src/Lakehold.Api
npm start --prefix web/lakehold-ui
```

`tests/Lakehold.Engine.Tests` covers catalog backup and restore. There are no frontend `*.spec.ts`
files yet. For every code change, run at least the affected build above, the narrow relevant tests
first, and the complete suite before handoff. For changes to tenant isolation, query streaming,
maintenance, storage, seeding, or the exit path, add focused tests rather than treating compilation
as sufficient proof.

```bash
dotnet test Lakehold.slnx
```

Integration tests skip unless their service is configured, so the default run needs no Docker. The
environment variables and container commands are in `README.md`. Run them before changing backup,
restore, scheduling, or storage-path handling: object stores have no directories to enumerate and
PostgreSQL metadata is not attached behind the catalog, and neither difference shows up at compile
time.

## Working approach

- Read the relevant implementation and documentation before changing behavior; do not infer
  provider or DuckLake semantics when they can be exercised directly.
- Keep changes scoped to the request and preserve unrelated user work and local runtime state.
- Prefer evidence-backed conclusions: build output for compilation, focused tests for behavior,
  and runtime or persisted-data checks for storage and migration claims.
- Do not commit, push, publish packages, run destructive maintenance, or change external systems
  unless the user asks for it.
- Report what changed, what was verified, and any remaining uncertainty or unverified runtime path.

