# Changelog

All notable changes to LakeHold are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and LakeHold follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [2.2.2] - 2026-08-09

Completes 2.2.1, which fixed MCP authorization-server discovery in the API but not at the edge.

### Fixed

- **The 2.2.1 redirects never reached the API in a real deployment.** Both nginx configurations
  proxy only the paths they name, and neither named the two authorization-server discovery
  documents, so `/.well-known/oauth-authorization-server` and `/.well-known/openid-configuration`
  were answered by nginx — 404 on the private Workbench, the SPA shell on the website — and the
  redirect the API had just learned to serve was never consulted. A deployment on 2.2.1 therefore
  behaves exactly as one on 2.2.0. Both configurations now proxy the two documents by prefix, so the
  path-suffixed forms reach the API too.

  This was caught by exercising the shipped configurations against a running API rather than by
  reading them; 2.2.1 was released on the strength of a syntax check and an end-to-end test that ran
  against the *development* proxy, which forwards the whole `/.well-known` prefix and so hid the gap.

  The prefixes are named individually rather than proxying all of `/.well-known/`, so ACME challenge
  paths continue to be served from disk.

## [2.2.1] - 2026-08-09

A fix for MCP browser sign-in, and a correction to what 2.2.0 said about it.

### Fixed

- **An MCP client could not sign in, and was sent to a page that has never existed.** Discovery
  reached LakeHold's RFC 9728 document and read the issuer out of it, then looked for
  authorization-server metadata **on LakeHold's own origin** rather than on the issuer it had just
  been told about. Finding none, the client fell back to the pre-RFC-9728 assumption that an MCP
  server is also its own authorization server, and opened `https://<lakehold>/authorize`. The 404
  that was the honest answer is exactly what triggered the guess.

  LakeHold still publishes no authorization-server metadata of its own — inventing one would mean a
  second copy of the issuer's document, free to drift from it. Instead the four discovery paths
  (`oauth-authorization-server` and `openid-configuration`, each with and without the MCP route
  suffix) now **redirect** to the configured authority, so the client reads the authorization
  server's own bytes. The request's flavour is preserved, so an OAuth-only authority is asked for
  `oauth-authorization-server` and an OIDC one for `openid-configuration`.

- **The public website served the resource document at one of its two RFC 9728 locations.** A client
  looks for the metadata of a resource that has a path at
  `/.well-known/oauth-protected-resource/<path>` and asks for that form *first*. nginx matched only
  the bare path, so in website mode the suffixed form fell through to the SPA and answered
  `index.csr.html` — HTML where the client expected JSON. Both nginx configurations now match the
  prefix. The website also answered `/authorize`, `/token`, and `/register` with the SPA shell,
  which reads to a client as "these endpoints exist"; they return 404 there now, as they always did
  on the private Workbench.

### Corrected

- **2.2.0 said the MCP discovery defect was "development only" and that production "was never
  affected". That was wrong**, and it was asserted without checking the nginx configurations. The
  private Workbench deployment was indeed unaffected, because it already answers unknown paths with
  404 — but the website/demo deployment shares the defect, for the same SPA-fallback reason, and is
  fixed above.

## [2.2.0] - 2026-08-08

A presentation release. LakeHold can be read on a light screen.

### Added

- **A light theme, across the website and the Workbench**, switched by an icon at the right-hand end
  of every header. The choice is remembered per browser, and a short script in the page head applies
  it before the first paint so a light-theme visitor is never shown a dark page first.
- The palette lives entirely in the token blocks at the top of `styles.css`: `:root` is dark and is
  what a document with no `data-theme` attribute gets, and `:root[data-theme='light']` re-points the
  same names without adding rules of its own. Adding a colour means adding a token, because a literal
  written into a component sheet is invisible to the light block and will stay dark on white.

  **The default remains dark, and deliberately does not follow the operating system.** Dark is what
  the product shipped with and what its screenshots and documentation describe; following the OS
  would have repainted every existing install for anyone whose machine is set to light.

### Changed

- **Accent as a fill is now a separate token from accent as text.** On a dark surface the brand amber
  works as both. On white, 13px amber type fails contrast while an amber *button* is the thing that
  keeps the product recognisable, so `--accent` is the text role and darkens under the light palette
  while `--accent-fill` keeps the brand yellow. Filled controls that had been drawing their label
  from `--surface-0` or `--bg` now use `--on-accent` and are consistent with each other in both
  palettes.
- Scrims, insets, and shadows became palette tokens for the same reason the colours are: black at
  40–72% is what lifts a dialog off a dark page and what reads as grime on a light one.

### Fixed

- **Disabled controls no longer bleach into a light page.** Every disabled button fades to 50–55%
  opacity, which against a near-black surface still reads as a control that is present but inert, and
  against white took a brand-yellow button's label down to roughly 2.3:1. Under the light palette
  disabled controls now state that look rather than fading into it. The dark treatment is unchanged.
- **Two light-palette contrast defects, found by auditing rendered text rather than by eye.**
  `--text-faint` measured 4.43:1 against the page surface, just under AA, and is darkened. And the
  selected navigation item was built as `color-mix(accent into a control grey)`, which reads as "tint
  this surface" but in fact darkens or lightens depending on which of the two is lighter — mixing
  amber into near-black lightens it into a highlight, while mixing the light palette's bronze into a
  grey pulls the surface toward the very text sitting on it. Selected and highlighted surfaces are
  now tokens each palette states outright. All four resolve, under the dark palette, to exactly the
  mixes they replaced.
- **An MCP client could not connect to the development stack.** The dev-server proxy forwarded only
  the one discovery document LakeHold publishes, so a client's remaining RFC 8414 and OIDC probes
  fell through to the Angular router and were answered with `index.html`. The client failed on
  `Unrecognized token '<'` and never reached the authorization server it had already been told
  about. The whole `/.well-known` prefix now goes to the API, which answers the unpublished paths
  with the 404 that means "read the issuer instead". Development only; production serves the API and
  the website through nginx and was never affected.

## [2.1.0] - 2026-08-08

An ingestion release. LakeHold reads Kafka, and the connector platform stops being something you can
only reach over HTTP.

### Added

- **Kafka Avro ingestion**, as a fifth built-in managed connector. It consumes a bounded window of
  Confluent-wire-format Avro records, resolves schemas through a Confluent-compatible HTTPS Schema
  Registry, and publishes into a governed DuckLake table on the existing incremental keyed-upsert
  path. Broker and Registry credentials are secret references only. Kafka is never contacted
  directly: a deployment-owned literal-IP TCP gateway carries broker traffic, a literal-IP HTTP(S)
  proxy carries Registry traffic, and the connector is refused outright until both are configured.
  This is at-least-once ingestion and says so — LakeHold publishes the batch *before* committing the
  broker offset, so a failed commit replays rather than loses, and never reports itself as a clean
  run. It is not a Debezium-style CDC connector: source deletions are not represented, and a
  tombstone advances the offset without staging a row.
- **`.avro` object-container upload** alongside CSV and XLSX, reading the file's own embedded writer
  schema. This is a developer import path and is unrelated to Kafka wire-format ingestion.
- **Managed connectors in the Workbench, and through MCP.** The connector platform was previously
  HTTP-only. The same durable definitions are now administered from a workbench panel and from a set
  of MCP tools, all three going through one validation boundary, so an agent cannot save a definition
  the administrator UI would refuse. Reading a connector is read-only; creating, editing, retiring,
  running, retrying, pausing, and resuming a connector sit behind **Allow write commands** in System
  Settings with `execute`, and `run_connector` is annotated destructive because for a full-snapshot
  definition a run replaces the target table. Secret *references* are accepted on every surface;
  secret values are not.
- **A recovery state for a publication whose source was never acknowledged.** When LakeHold commits
  a batch to DuckLake but the source acknowledgement then fails, the run is recorded as
  `published-source-acknowledgement-pending` rather than as either a success or a failure, and the
  connector stops scheduling until an operator retries it explicitly. Neither of the alternatives is
  true: the data is queryable, and the offset was not committed.
- **System Settings → New workspace** provisions additional tenant boundaries without dropping to
  HTTP. A successful create refreshes the Workbench workspace list and preselects the new workspace
  in **New catalog**, so the two-step flow can be completed in place. Workspace validation is shared
  with first run, and independent provisioning panels remain available if MCP settings fail to load.

### Changed

- **Writing through MCP is gated by what a tool does, not by its name.** The `AllowWrites` gate
  matched the literal tool name `execute`. It now reads each tool's own `readOnly` annotation, in
  both the discovery filter and the call filter, so a mutating tool added later is covered the moment
  it is registered rather than when somebody remembers to extend a list. No tool that was reachable
  in 2.0.2 changes behaviour; this is what lets the connector tools above ship gated.
- The workspace-administration feature is now **Users** end to end: `UsersComponent`, the
  `lh-users` selector, the `users` navigation destination, page copy, API descriptions, and operator
  documentation all use the same name instead of retaining internal **People** identifiers.

### Fixed

- **An unhealthy query planner disappeared instead of explaining itself.** A configured planner that
  failed discovery — a missed one-second deadline, a planner key the API and the compiler disagree
  on, a container still starting — was dropped from `/query-languages` with the only trace in the
  API's logs. In the Workbench that is indistinguishable from a language nobody ever installed, and
  the person looking at the selector is not the person reading the logs. A configured planner now
  stays in the selector, marked unavailable and carrying an operator-actionable reason, and the API
  logs a warning naming the planner. The language keeps the display name and editor mode it had when
  it was last healthy, so a compiler that misses one deadline does not also lose its name. SQL is
  unaffected either way: it runs in the API process and has no planner to be unhealthy.

## [2.0.2] - 2026-08-05

An operations release. Nothing in the application changed; what changed is that pinning a release
works the way every instruction says it does.

### Fixed

- **A released version could not be pinned as documented.** Every operator instruction quotes the
  git tag — `LAKEHOLD_TAG=v2.0.1` — but the release workflow published only the bare `2.0.1`, so
  copying a release's own version string answered `manifest unknown` and the only tag that worked
  was the `latest` the same documentation tells you not to track. Releases now publish `2.0.2`,
  `2.0`, `v2.0.2`, and `v2.0`, and either form of a version pins the identical build. The 2.0.0 and
  2.0.1 images were retagged rather than rebuilt, so their `v`-prefixed names resolve to the same
  digests already in the registry.

### Added

- A retag workflow that points a new tag at an already-published manifest instead of rebuilding it.
  A rebuild from the same commit produces a different digest, which leaves an operator comparing two
  names for the same release and finding them unequal for no visible reason. It refuses a source
  that does not exist, refuses to move `latest` without an explicit opt-in, and proves both tags
  resolve to one digest before reporting success.

### Changed

- Every deploy and recovery example in the operations guide and disaster-recovery runbook names a
  real published tag instead of a placeholder, and a test enforces it. A placeholder cannot be run,
  so it cannot fail — which is why the defect above was found by an operator's copy-paste rather
  than by the runbook. Full-state recovery now exports the tag once, checks it resolves before the
  recovery depends on it, and guards the two commands that bind an image so continuing in a fresh
  shell cannot silently deploy `latest` during an incident.

## [2.0.1] - 2026-08-05

### Added

- Browser coverage for three journeys that previously only asserted their panel opened: the
  saved-query publish lifecycle, importing a CSV as a table, and the instance MCP settings. Each
  ends at evidence outside the component that produced it — a published view answering SQL, an
  imported table queried back by row count, and a disabled MCP endpoint answering the live request.

### Changed

- The getting-started guide covers signing in: the seeded users and what each reaches, the identity
  provider's URL, the API-token path for machines, and the fact that signing out of LakeHold does
  not end the provider's session.
- Row- and column-level security is specified in the enterprise roadmap rather than listed as a
  checkbox, including why the current architecture cannot express it and what would have to change.
- `make dev` starts the C# LINQ planner, so both query languages are available without a second
  command and the browser suite can pass against the stack the guide tells you to start.
- The web package version tracks the product release, and the deploy example in the operations
  guide names a current tag rather than `v1.0.1`.

### Fixed

- A request the server could not bind — a missing or unparseable query parameter — answered `500`
  in development and a bare `400` with no body in production. Both now answer `400` as RFC 9457
  `problem+json` naming the parameter. The `500` was the more serious half: `5xx` is the one class
  an SDK is entitled to retry, and retrying a malformed request cannot succeed.
- `make stop` acted only on the deployment stack, so stopping a development stack was a silent
  no-op that left it holding its ports. `make status` and `make logs` had the same blind spot. All
  three now act on whichever stack is running.

## [2.0.0] - 2026-08-04

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

### Removed

- **`Lakehold:Auth:RequireAuthentication` (and `LAKEHOLD_REQUIRE_AUTH`).** This is the breaking
  change, and the reason for the major version. The switch is gone rather than defaulted
  differently: because it defaulted to off, the authorization layer was inert in the configuration
  developers actually ran. Authentication is now unconditional on every surface, so a node that
  served credential-less requests under 1.4.0 refuses them under 2.0.0.

### Changed

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

[Unreleased]: https://github.com/skuirrels/LakeHold/compare/v2.2.2...HEAD
[2.2.2]: https://github.com/skuirrels/LakeHold/compare/v2.2.1...v2.2.2
[2.2.1]: https://github.com/skuirrels/LakeHold/compare/v2.2.0...v2.2.1
[2.2.0]: https://github.com/skuirrels/LakeHold/compare/v2.1.0...v2.2.0
[2.1.0]: https://github.com/skuirrels/LakeHold/compare/v2.0.2...v2.1.0
[2.0.2]: https://github.com/skuirrels/LakeHold/compare/v2.0.1...v2.0.2
[2.0.1]: https://github.com/skuirrels/LakeHold/compare/v2.0.0...v2.0.1
[2.0.0]: https://github.com/skuirrels/LakeHold/compare/v1.4.0...v2.0.0
[1.4.0]: https://github.com/skuirrels/LakeHold/compare/v1.3.0...v1.4.0
[1.3.0]: https://github.com/skuirrels/LakeHold/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/skuirrels/LakeHold/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/skuirrels/LakeHold/compare/v1.0.1...v1.1.0
[1.0.1]: https://github.com/skuirrels/LakeHold/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/skuirrels/LakeHold/releases/tag/v1.0.0
