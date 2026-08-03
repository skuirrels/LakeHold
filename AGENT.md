# LakeHold repository guidance

This file is the durable working context for coding agents in this repository. Keep it aligned with
the live code and with `README.md` and `docs/ARCHITECTURE.md` when the architecture changes.

## Product and stack

LakeHold is a self-hostable, multi-tenant lakehouse built on DuckDB and DuckLake. It deliberately
trades managed elasticity for infrastructure control, open Parquet storage, and first-class .NET
integration.

- Backend: .NET 10, ASP.NET Core minimal APIs, EF Core 10, DuckDB.EFCoreProvider.
- Frontend: Angular 22 with TypeScript 6 and npm.
- Local orchestration: Docker Compose for backing services; the API and dev server run on the host.
- Local durable state: `.lakehold/` below the API project unless configuration overrides it.
- Package versions are managed centrally in `Directory.Packages.props`; project files should not
  add versions to individual `PackageReference` items.

## Repository map

- `src/Lakehold.Engine`: data plane, Duckling sessions, dynamic SQL execution, catalog browsing,
  DuckLake maintenance, verified eject bundles (`CatalogEject`), and the change feed (`ChangeFeed`).
  `MetadataExporter` holds the metadata-table copy shared by backup and eject.
- `src/Lakehold.ControlPlane`: modelled EF Core state for tenants, catalogs, saved queries, managed
  connector definitions/runs, and query/audit history.
- `src/Lakehold.Api`: HTTP contracts, minimal-API endpoints, configuration, demo seeding, managed
  REST/gRPC/PostgreSQL/HubSpot ingestion under `Connectors/`, the CDC webhook dispatcher under `Cdc/`, and the
  PostgreSQL wire endpoint under `PgWire/`.
- `src/Lakehold.AppHost`: legacy Aspire composition. Retained but no longer the documented way to
  run the product — `compose.yaml` plus the two host processes is. Do not add to it.
- `src/Lakehold.ServiceDefaults`: health, resilience, service discovery, and telemetry defaults.
- `web/lakehold-ui`: Angular workbench, catalog explorer, result grid, landing page, and the docs
  page (`/docs`). The docs page renders `src/app/docs.content.md` at runtime (via `marked`, with a
  `.md` text loader configured in `angular.json`); that one Markdown file is the single source for
  both the in-app page and the getting-started guide read on GitHub — edit it, not two copies.
  The DuckDB.EFCoreProvider surface follows the same split across two routes: `/provider` is the
  pitch and `/provider/docs` renders `src/app/provider.content.md`. They are separate pages so that
  a "Docs" link never means the provider's documentation on one page and LakeHold's on the next; the
  provider pages name both destinations. A new route needs an entry in `public/sitemap.xml` too.
- `docs/ARCHITECTURE.md`: architectural rationale and current product boundaries.
- `docs/EXIT-PATH.md`: verified open-format exit procedure and Parquet caveats. Eject automates that
  procedure; keep the two consistent.
- `docs/OPERATIONS.md`: production operating model and entry point for day-two work. Its
  `docs/runbooks/` documents incident response, disaster recovery, and monitoring/alerting. Keep
  commands, health semantics, metric names, storage boundaries, and recovery limitations aligned
  with the production Compose file and runtime.
- `docs/PROVIDER-FEEDBACK.md`: provider capabilities and why the data plane uses its dynamic API.
- `docs/POSTGRES-WIRE.md`: the wire protocol surface, its connection model, and what is
  deliberately unimplemented. Update it with the endpoint.
- `docs/AUTHENTICATION.md`: the phased plan for API authentication, now fully implemented — API
  tokens, provisioning, read-only-by-attachment, audit, wire convergence, OIDC browser sessions,
  system-administrator claims, and roles. Browser cookie keys are shared through PostgreSQL. A route
  declares a `Capability` and `LakeholdAuthorizationFilter` enforces it in one place. Note that
  `Lakehold:Auth:RequireAuthentication` still defaults to **false**, so a token-less request falls
  back to trusting the route until an operator turns it on. Read it before adding any surface that
  resolves a tenant.
- `docs/IDENTITY-PROVIDER-SETUP.md`: the operational companion to the above — how a human actually
  signs in, and how to put Keycloak or another provider behind the Workbench. Covers the bootstrap
  token, first-workspace provisioning, the claim mappers LakeHold reads (`tenant`, `role`,
  system-admin), and why an empty `Lakehold:Oidc:Audience` accepts every token that issuer minted.
- `docs/PUBLIC-API.md`: the phased spec for the public HTTP control API and first-party Java, Go,
  .NET, and Python SDKs — time travel and the whole lakehouse. Builds on `docs/AUTHENTICATION.md`
  (auth is its gate); the cross-cutting API conventions (versioning, `problem+json`, pagination,
  async jobs), SDK boundary, and shared conformance gates live here.
- `docs/CONNECTORS.md`: the managed full-snapshot and incremental connector contract, administration
  API, adapter SDK, checkpoints, security boundaries, limits, and explicit limitations. Keep it aligned with `Connectors/`, the
  connector DTOs, and `Lakehold:Connectors` configuration.
- `docs/ENTERPRISE-DATA-PLATFORM-ROADMAP.md`: the staged ingestion, governance, public API/client
  SDK, semantic, and enterprise-consumption plan. A partial connector or unpublished client must not
  be described there as completing P1.
- `docs/MCP.md`: the phased spec and running record for the MCP server under `src/Lakehold.Api/Mcp/`.
  Phases 1-5 have landed: five read-only tools, a schema resource, and optional writes. Development
  enables it by default; instance-level System Settings persist live controls in PostgreSQL and use
  the existing public tenant-token endpoint to mint scoped client credentials. Records
  why the dependency is the MCP C# SDK and *not* Microsoft Agent
  Framework (LakeHold is the server, not the agent), which tools are deliberately withheld from an
  agent and why, and how to connect Claude Code and Codex. Read it before adding an agent-reachable
  surface.
- `docs/UI.md`: the phased spec and running record for the web surfaces beyond the SQL IDE. Its
  physical layer — table sizes, Parquet files, delete overhead, partitions, and maintenance advice —
  is read from DuckLake's catalog rather than by listing the data path. The unified table inspector
  also profiles live logical columns and bounded distributions, optionally at a snapshot. Records
  why a raw object browser is the wrong build. Read it before adding a workbench surface.
- `docs/COMPETITIVE-RESEARCH.md`: a **dated** snapshot of competitor releases, the DuckLake roadmap,
  and ranked demand from the upstream trackers. It is the evidence behind the positioning and feature
  matrix in `docs/ARCHITECTURE.md`; that document states the position, this one says when it was last
  checked. Re-gather rather than amend when it ages, and never cite a claim from it without its date.

## Architectural invariants

Preserve these unless the task explicitly changes the architecture and updates its documentation.

1. The control plane and data plane are split by workload. `ControlPlaneContext` uses Npgsql and
   PostgreSQL for shared, modelled application state; `LakeContext` uses DuckDB.EFCoreProvider and
   DuckLake for arbitrary tenant SQL.
2. `ControlPlaneContext` is the modelled EF Core context on PostgreSQL and uses migrations. Native
   DuckDB control-plane files are supported only as legacy import sources and isolated test adapters,
   not as a production fallback.
3. `LakeContext` is intentionally model-less. Arbitrary result shapes must use the provider's
   streaming `SqlQueryDynamicRawAsync` path; do not add fake entity types or reintroduce a parallel
   raw `DuckDBConnection` stack without a demonstrated provider gap.
4. A `Duckling` is the tenant isolation and compute unit. Isolation comes from which catalog is
   attached to a session. Do not treat parsing, filtering, or rewriting submitted SQL as the
   security boundary. `StatementVerb` reads a statement's leading keyword only to choose how its
   outcome is *reported* — counted DML (`INSERT`/`UPDATE`/`DELETE`/`MERGE` without `RETURNING`) runs
   as a non-query because the provider's dynamic path has no affected-row count. A statement it does
   not recognise must always fall back to the ordinary streaming path, never be refused, and user SQL
   must never go through EF's raw-SQL formatting, which reads braces in struct and map literals as
   format placeholders.
5. A Duckling owns a non-thread-safe `DbContext` and a single-writer DuckDB instance. Query and
   maintenance access must remain serialised through the session gate. The gate is now a LakeHold
   choice rather than a provider constraint — reads scale when each concurrent operation owns a
   separate context and connection, measured in `docs/PROVIDER-FEEDBACK.md` — but it stays until a
   per-tenant read pool exists to replace it. Any such pool is for PostgreSQL metadata: the provider
   documents a DuckDB metadata file as a single-client profile.
6. Query results are streamed and capped by `LakehouseOptions.MaxRowsPerResult`. Preserve
   cancellation, statement timeouts, and early termination so large results are not fully
   materialised before truncation. The cap belongs to paths that *materialise* a result — it bounds
   a JSON response built in memory before it is sent. `Duckling.StreamQueryAsync`, which the wire
   endpoint uses, honours the same purpose by construction and so does not apply it: rows are encoded
   to the socket and forgotten. Do not add the cap back to a streaming path, and do not remove it
   from a materialising one.
7. Catalog and extension identifiers cannot always be parameterised. Use `SqlIdentifier`, and pick
   the right half of it: `Quote` **validates** a bare identifier against an allow-list and returns it
   *unquoted*, for a trust boundary where a malformed name should be rejected — a catalog name from a
   control-plane record, an extension name. `QuoteName` **escapes** into a double-quoted identifier,
   for a name that came out of the catalog and is going back into a statement. Tenants create tables
   called `order-items`, `my.table`, and `select`; DuckLake stores all three, so validating one of
   those is not a safety property, it is a read that fails on a catalog the engine is happy with.
   Parameterise ordinary values wherever the underlying API permits it.
8. Object-store credentials belong in provider connection configuration. Never persist them in a
   catalog, response, source file, or log. Each generated DuckDB secret must be scoped to the
   tenant-qualified catalog data, backup, or eject prefix it serves; a deployment-wide bucket
   credential must never be usable against an unrelated prefix from tenant SQL.
9. Read-only additional catalogs must remain read-only. Do not widen write access to implement
   sharing or cross-catalog queries. A share is attached by path or by secret name according to its
   `AttachedCatalog.MetadataKind`, exactly as the primary catalog is.
10. Snapshot expiry and old-file cleanup are destructive and must remain dry-run by default with an
    explicit apply/confirmation path. Flush and compaction are non-destructive maintenance, and are
    the only operations that commit: they run inside a transaction labelled `lakehold maintenance: …`
    so platform-initiated snapshots stay distinguishable from a tenant's own writes.
11. Data, backup, and eject locations are tenant-qualified:
    `<root>/<tenant-key>/<catalog>/...`. Catalog display names are unique only within a tenant, so
    omitting the tenant key aliases durable state across tenants. Catalog backups live under
    `BackupRoot`, a sibling of the data root and never a child of it. Anything under the data path
    that the catalog does not reference is a candidate for DuckLake's orphan cleanup, so a nested
    backup deletes itself once it ages.
12. Restore never overwrites an existing catalog, and never restores a generation with no manifest.
    An interrupted export missing `ducklake_delete_file` would silently reinstate deleted rows. The
    metadata table list is therefore always *discovered*, never hard-coded: DuckLake stages small
    commits in a per-table `ducklake_inlined_data_<schema>_<table>` named at run time, and those rows
    are committed data not yet in Parquet. From DuckDB 1.5.4 the metadata catalog is hidden from
    `duckdb_tables()` and every other introspection surface, so `MetadataExporter` enumerates over an
    independent read-only connection — and refuses to write a manifest if it finds no tables at all,
    because an empty backup that reports success is the failure this invariant exists to prevent.
13. Remote metadata is addressed by DuckDB secret name, never by connection string — for the primary
    catalog and for shares alike. The provider rejects a non-file metadata path. PostgreSQL and
    DuckLake profile secrets exist only while the provider attaches the catalog, then are dropped
    before tenant SQL can run. A PostgreSQL credential may be recreated only under the Duckling gate
    for a trusted metadata export or maintenance lease and must be dropped before releasing the gate.
    No credential reaches a catalog record, response, source file, or log.
14. The maintenance lease belongs in the `lakehold` schema, not `public`. It must not collide with a
    DuckLake migration, and it must not be swept into a catalog backup.
15. Eject exports data by re-materialising each table through the catalog
    (`COPY (SELECT * FROM table) TO …`), never by copying the data path. Only the former applies
    merge-on-read deletes, collapses superseded update rows, includes inlined data, and drops the
    `_ducklake_internal_*` columns. Eject is read-only: it must not mutate the catalog, so it works
    on a read-only share and needs no flush first.
16. An eject bundle's manifest is written last and only after every table's re-read row count matches
    the catalog's. A verification failure must abort before the manifest exists — an unverified
    bundle must never be able to present itself as complete, exactly as with a backup generation.
17. The eject signing key and a subscription's webhook secret are secrets. The key comes from
    configuration and is never written to a manifest, response, or log; the subscription secret is
    persisted only because signing requires it, and must never appear in any DTO or log.
18. CDC delivery is at-least-once with a resumable cursor. Windows advance one snapshot at a time and
    `LastDeliveredSnapshot` moves only after a 2xx, so a failing consumer replays rather than skips.
    `ducklake_table_changes` is inclusive at both ends, so the next window opens at `L + 1`.
19. The credential names the tenant; the route segment is validated against it, never trusted. One
    `LakeholdAuthorizationFilter` enforces a route's declared `Capability`, and subject is always
    checked before capability so an unreachable tenant is a **404, not a 403** — a 403 would confirm
    it exists. A token's plaintext is never stored: only its public prefix and a SHA-256 hash, so
    reading the table yields nothing usable. The single exception to "never log a credential" is the
    bootstrap token, logged once because it is otherwise unrecoverable and grants provisioning only.
20. Capability is expressed as attachment wherever it can be. A read-only credential produces a
    read-only *attachment*, so a write fails in the engine rather than in a policy check that clever
    SQL might route around — the same reasoning as invariant 4. `DucklingPool` therefore keys sessions
    by catalog **and attachment mode**: sharing one session between a read-only and a read-write
    credential would silently hand the former a writable handle.
21. An agent-reachable surface declares its capability like a route and always requires a credential.
    An MCP tool names a `Capability` and the *same* policy that guards the HTTP route enforces
    it — the rules live in one transport-neutral place, never copied into a second dispatch, or the
    404-not-403 reasoning in invariant 19 drifts between them. Unlike every other surface, MCP refuses
    a token-less call even while `Lakehold:Auth:RequireAuthentication` is false: a surface whose
    purpose is letting an autonomous agent run SQL cannot also trust the route. See `docs/MCP.md`.
22. A table-data restore preserves the current table definition. Never implement it as
    `CREATE OR REPLACE TABLE AS SELECT … AT (…)`, which drops current defaults and nullability, or as
    `DELETE` followed by a direct historical read, which can resolve through the pending delete.
    `TableRestore` stages historical rows first, inserts shared columns through the existing table,
    and owns one labelled transaction under the Duckling gate so any incompatibility rolls back.
    The API returns a dry-run plan before `apply: true`, and apply requires that plan's current
    snapshot id so an intervening commit forces a fresh review; read-only callers never receive the
    action.
23. Managed connectors publish full snapshots or keyed incremental deltas atomically. Definitions,
    schedules, fenced claim generations, source versions, committed/proposed checkpoints, replay
    keys, outcomes, quality evidence, and lineage are durable PostgreSQL state; response bytes may
    use node-local disk only as bounded disposable scratch. A full refresh replaces its DuckLake
    target and an incremental refresh performs an idempotent keyed upsert only after required-column,
    minimum-row, not-null, and explicit schema-policy gates pass in the same labelled transaction.
    An adapter may propose a checkpoint, but only the PostgreSQL publication fence advances it after
    DuckLake commits; a completion failure therefore replays rather than skips. First publication
    must refuse an existing unmanaged target and atomically mark a created target so an unconfirmed
    first commit is recognizable on replay. Archival must retain the connector definition and
    run lineage. Adapters translate protocols only: they must reuse the shared egress policy, limits,
    orchestration, publication, secret resolution, and error sanitisation, and no credential or
    source record may enter a definition, response, audit record, trace, or log. Record counts come
    from the shared writer, never an adapter assertion. A credential reference resolves only under
    an exact operator-owned tenant/catalog/reference/destination binding. Ordered polling requires
    an explicit commit-monotonic source contract, and provider result/rate ceilings must fail closed
    or be handled by bounded windows and replay overlap rather than silently truncating data.

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

- The human-facing product name is **LakeHold**. Website copy, page titles, metadata, README prose,
  and documentation must use that capitalisation. Preserve the existing `Lakehold` spelling in
  technical identifiers such as .NET namespaces and projects, configuration keys
  (`Lakehold:Auth`, `Lakehold__...`), image/repository paths, URLs, and filenames.
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

Configuration is split by whether a value is a secret, and new settings must follow it:

- **`appsettings*.json`** — application configuration, including the OpenTelemetry endpoint and
  service name. OpenTelemetry reads its standard `OTEL_*` keys from `IConfiguration`, so they work as
  plain top-level settings and need no environment variable.
- **`compose.yaml`** — service ports, users, and database names, written as inline
  `${VAR:-default}` defaults so they stay overridable without living in `.env`.
- **`.env`** — secrets only, and gitignored. It is loaded by the API in `Program.cs` before the host
  is built, by the test suite through a module initializer, and by compose for substitution.

`.env.example` is the checked-in template and the place to document a new secret. Never commit a
`.env`, and never move a real credential into `.env.example`. Adding a non-secret setting to `.env`
is the common mistake: if every developer would set it identically, it belongs in source control.

`compose.production.yaml` is the deployment stack: images pulled from GHCR, non-root, no source, no
watcher, the API unpublished behind nginx, public website routes disabled, demo seeding off, and —
unlike the application default — authentication required. It serves the private Workbench only;
`compose.demo.yaml` is the sole overlay that selects website mode and exposes the public routes. The
production file is self-contained so that installing is a download rather than a build;
`compose.build.yaml` is the override that adds a build context for a from-source deploy, and the two
Dockerfiles are published for amd64 and arm64 by `.github/workflows/release.yml` on a `v*` tag. Keep
the image names in the compose file and the workflow in step. `compose.yaml` runs the whole *development*
stack: the API and Angular dev server from stock SDK images with the
source bind-mounted, plus PostgreSQL, MinIO (and the bucket creation the S3 tests depend on), and a
trace viewer. `docker compose up` then serves the website at <http://localhost:5399>; the API is on
`:5200`. Running the two app services on the host works identically — start the backing services
only and use `dotnet run` / `npm start`.

The dev server's proxy target comes from `NG_API_URL` (`web/lakehold-ui/proxy.conf.mjs`), falling
back to `localhost:5200`. It has to stay dynamic: inside a container `localhost` is the UI container,
so a hard-coded target proxies to nothing and every API or MCP call fails with a 500.

Do not edit or commit build output, dependency caches, IDE state, or runtime lakehouse data:

- `bin/`, `obj/`, `dist/`, `node_modules/`, `.angular/`, `.npm-cache/`
- `.idea/`, `.vscode/`, `.DS_Store`
- any `.lakehold/` directory, including catalog databases and Parquet files

Treat existing `.lakehold/` data as user state. Do not delete, reseed, migrate, or overwrite it
unless the user explicitly authorises that operation and the impact is understood.

## Build, run, and verification

Requirements are Docker, the .NET 10 SDK, and Node.js 20 or newer.

```bash
# Restore and build all backend projects
dotnet build Lakehold.slnx

# Reproducible frontend dependency install, when needed
npm ci --prefix web/lakehold-ui

# Production frontend compilation
npm run build --prefix web/lakehold-ui

# Backing services: PostgreSQL, MinIO, and a trace viewer
cp .env.example .env
docker compose up -d

# The product itself, on the host: API on :5200, UI on :5399
dotnet run --project src/Lakehold.Api
npm start --prefix web/lakehold-ui
```

`tests/Lakehold.Engine.Tests` covers catalog backup, restore, and the storage view. The frontend has
`*.spec.ts` suites for the workbench panels, run by the Angular unit-test builder on Vitest. For every
code change, run at least the affected build above, the narrow relevant tests first, and the complete
suite before handoff. For changes to tenant isolation, query streaming, maintenance, storage, seeding,
or the exit path, add focused tests rather than treating compilation as sufficient proof.

```bash
dotnet test Lakehold.slnx
```

```bash
npm test --prefix web/lakehold-ui
```

A component test earns its place by failing when the behaviour breaks: the panels' hardest bugs —
a signal effect that re-runs and undoes its own work, an error banner left over from another panel —
pass the type checker and the production build. Mutate the source and confirm the test goes red before
trusting it.

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
- **Always work on a branch, never directly on `main`.** Create it before the first edit, not before
  the first commit: work started on `main` has to be moved later, and moving it is where changes get
  lost. If you find yourself on `main` with uncommitted work, branch immediately — `git switch -c`
  keeps the working tree.
- One branch per piece of work. When the task changes into something the branch name no longer
  describes, branch again rather than letting one branch accumulate unrelated commits.
- Branch names describe the work, never the author or the tool that produced it. Use a `feature/`
  prefix and a short kebab-case summary — `feature/brand-mark-component`. Never a `claude/` prefix
  or a generated name, including the default an agent session or worktree proposes: rename the
  branch before the first commit. Use `fix/` or `docs/` where either reads more truthfully.
- Stage deliberately. `git add -A` and `git add <dir>` sweep up whatever else is in the tree, which
  on a shared checkout means committing someone else's in-progress work under your message. Name the
  paths you changed, and read `git diff --cached` before committing.
- Report what changed, what was verified, and any remaining uncertainty or unverified runtime path.
