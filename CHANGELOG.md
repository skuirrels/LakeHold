# Changelog

All notable changes to LakeHold are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and LakeHold follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.5.0] - 2026-08-04

### Added

- Tenant membership: a `TenantMember` record decides what a signed-in identity reaches, and an
  administrator admits, demotes, suspends, and removes people under **People** in the Workbench.
- An identity provider bundled in the development stack and enabled by default, with a seeded
  realm, a confidential client, and two users covering the workspace-owner and
  instance-administrator paths.
- `docs/IDENTITY-PROVIDER-SETUP.md`: first-run production setup, adding people, swapping the
  identity provider for your own, and connecting clients and agents.
- An enterprise positioning review, and a `make prune-worktrees` target for finished agent
  worktrees.

### Changed

- **Authentication is always required.** The `Lakehold:Auth:RequireAuthentication` switch is gone
  rather than defaulted differently: because it defaulted to off, the authorization layer was inert
  in the configuration developers actually ran.
- Authorization is owned by the product, not the identity provider. A `tenant` claim is honoured
  once to admit a first arrival; after that the membership record wins, so a provider re-asserting
  a stale role cannot undo a decision made in LakeHold. Instance administration stays a provider
  claim by design, so a workspace owner cannot promote themselves.
- A Workbench credential can outlive the browser tab, by choice, and the API token box is no longer
  presented as a sign-in.
- The home page is the site's answer to a search for the product name: it alone carries the
  `SoftwareApplication` entity, and every other indexable page publishes as a document about it.
- The public API SDKs are qualified against the released API, and NuGet packages were updated.

### Fixed

- Races in token administration.
- The brand casing check reported success without running wherever ripgrep was absent — including
  on every CI run, so the spelling rule had never actually been enforced.
- Documentation, runbooks, and website copy that still described authentication as opt-in, including
  a README instruction and an incident-response step naming a setting that no longer exists.
- GitHub Actions runtime warnings.

### Upgrade notes

- `Lakehold:Auth:RequireAuthentication` and `LAKEHOLD_REQUIRE_AUTH` no longer exist. Setting either
  has no effect. Every deployment now requires a credential on every surface.
- Before upgrading, make sure callers hold tokens. A deployment that relied on the old default was
  accepting token-less requests and trusting the route; those requests will now be refused.
- To publish something without a credential, configure `Lakehold:Auth:DemoTenant` and
  `Lakehold:Auth:DemoCatalog`. That is a real read-only identity scoped to one catalog, not a
  bypass; leaving either empty fails closed.

## [1.4.0] - 2026-08-03

### Added

- A public HTTP control API with first-party Java, Go, .NET, and Python SDKs.
- Streaming delivery for the enterprise data platform, completing the Priority 1 ingestion path.

## [1.3.0] - 2026-08-02

### Added

- Managed data connectors: REST and gRPC full snapshots, PostgreSQL and HubSpot keyed incremental
  refreshes, fenced checkpoints, schema-policy gates, quality evidence, and lineage.
- Complete LINQ Workbench documentation and an enterprise data platform delivery plan.

### Changed

- LINQ is enabled in the demo by default.

## [1.2.0] - 2026-08-02

### Added

- An isolated LINQ Workbench: C# LINQ translated by an out-of-process DuckDB.EFCoreProvider
  planner, with generated SQL and diagnostics.
- Browser CSV and XLSX table imports.
- Authenticated MCP access administration, and DuckDB replication.
- A `make dev` target for the local development stack.

### Changed

- Refreshed the Workbench navigation shell, and kept collapsed navigation reachable.
- Hardened change data capture.

### Fixed

- PostgreSQL-backed demo startup.
- CSV import retry handling, and the authentication tone on the comparison page.

## [1.1.0] - 2026-07-28

### Added

- Catalog-scoped reusable saved queries with optimistic revisions, read-only execution, and an
  explicit publish, republish, and unpublish lifecycle for DuckLake views.
- Unified data history for inspecting snapshots, schema transitions, changes, and historical rows
  from the Workbench.
- Table detail views with live column profiles, bounded distributions, physical storage information,
  and snapshot-aware inspection.
- Dry-run-first table restore workflows that preserve the current table definition and reject stale
  plans.
- Versioned EF Core migrations for the PostgreSQL control plane and a transactional, dry-run-first
  importer for legacy DuckDB control-plane databases.
- Production operations documentation, incident-response and disaster-recovery runbooks, monitoring
  guidance, and native documentation routes.
- Website-only analytics that remain disabled for the private Workbench.

### Changed

- PostgreSQL is now the required production control plane and the default metadata store for new
  DuckLake catalogs.
- Catalog data, backups, eject bundles, secrets, and warm sessions are tenant-qualified so tenants
  may safely use the same catalog name.
- Deployment and test stacks now exercise PostgreSQL control-plane migrations and S3-compatible
  storage as required integrations.
- Production deployment, public website routing, mobile navigation, and documentation presentation
  were hardened.

### Fixed

- Redirected the `www` website hostname to the canonical apex domain.
- Corrected mobile landing-page header spacing and clarified the MCP server introduction.

### Upgrade notes

- Configure both `ConnectionStrings:ControlPlane` and `ConnectionStrings:DuckLakeMetadata` before
  starting LakeHold 1.1.0.
- Take a consistent off-host backup before upgrading and deploy a pinned
  `LAKEHOLD_TAG=v1.1.0` rather than tracking `latest`.
- For an existing DuckDB control plane, stop every legacy writer and follow the dry-run-first
  [legacy import procedure](docs/POSTGRES-AND-STORAGE.md#importing-a-legacy-duckdb-control-plane).
  The importer preserves existing catalog metadata and data locations; moving DuckLake metadata to
  PostgreSQL is a separate data-plane migration.

## [1.0.1] - 2026-07-26

### Changed

- Separated the private production Workbench from the public read-only demo website.
- Added distinct production and demo Compose behavior, nginx routing, deployment documentation, and
  browser coverage for both surfaces.

## [1.0.0] - 2026-07-26

### Added

- First stable LakeHold release: a self-hostable, multi-tenant lakehouse built on DuckDB, DuckLake,
  open Parquet storage, .NET 10, and Angular.
- Tenant-isolated SQL execution with streaming, cancellation, statement timeouts, bounded
  materialised results, and query history.
- API tokens, roles, catalog-scoped access, audit records, OIDC support, and a shared authorization
  policy across HTTP, PostgreSQL wire, and MCP surfaces.
- PostgreSQL wire connectivity with TLS and per-tenant credentials.
- Catalog backup and restore, verified and optionally signed eject bundles, snapshot maintenance,
  storage inspection, scheduling, and Debezium-free CDC.
- A SQL Workbench, catalog explorer, storage and operations panels, documentation website, and
  provider documentation.
- Production API and web container images for Linux amd64 and arm64, Compose deployment, health
  checks, telemetry, and a reproducible end-to-end test suite.

[Unreleased]: https://github.com/skuirrels/LakeHold/compare/v1.5.0...HEAD
[1.5.0]: https://github.com/skuirrels/LakeHold/compare/v1.4.0...v1.5.0
[1.4.0]: https://github.com/skuirrels/LakeHold/compare/v1.3.0...v1.4.0
[1.3.0]: https://github.com/skuirrels/LakeHold/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/skuirrels/LakeHold/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/skuirrels/LakeHold/compare/v1.0.1...v1.1.0
[1.0.1]: https://github.com/skuirrels/LakeHold/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/skuirrels/LakeHold/releases/tag/v1.0.0
