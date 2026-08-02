# Enterprise Data Platform delivery plan

This is the delivery and status record for positioning LakeHold as a focused Enterprise Data
Platform (EDP), not merely a SQL endpoint over open storage.

**Status date:** 2 August 2026

**Status vocabulary:**

- **Implemented in source** — code and tests are present and verified in repository source, but no
  published release is claimed.
- **Shipped** — available on the main branch and in a released artifact.
- **Partial** — useful capability exists, but the stated EDP acceptance boundary is not met.
- **Not started** — no production implementation is claimed.

> LakeHold v1.3.0 ships the managed connector foundation and connector platform. A real connector
> migration and refresh in a deployed v1.3.0 environment remains an explicit post-release evidence
> gate; the release alone does not claim that operational proof.

## Executive status

| Workstream                          | Status                    | Completed boundary                                                                                                                                                                                    | Remaining boundary                                                                                                                                   |
| ----------------------------------- | ------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------- |
| P1.1 Managed ingestion foundation   | **Shipped**               | v1.3.0 includes REST JSON-array/NDJSON and gRPC full snapshots, durable definitions/runs, schedules, fencing, target ownership, quality, egress, scratch controls, telemetry, API outcomes, and production-path tests | Post-release migration and real connector-refresh deployment evidence                                                                                 |
| P1.2 Connector platform             | **Shipped**               | v1.3.0 includes the versioned adapter SDK/manifest, commit-fenced checkpoints, replay-safe keyed upsert, retry/dead-letter lifecycle, mappings, schema policy, external secrets, approved auth, PostgreSQL, and HubSpot | Deployment evidence and a broader production-certified adapter catalogue                                                                              |
| P1.3 Catalog and governance         | **Partial**               | Owners, descriptions, tags, quality policy, audit, and connector run lineage exist on initial surfaces                                                                                                | Stable identity for every asset, search, classification, freshness, policy administration, and end-to-end lineage graph                              |
| P1.4 Semantic and consumption layer | **Partial**               | HTTP, Workbench, MCP, EF Core, PostgreSQL wire for psql/DBeaver/Npgsql, and saved-query publication                                                                                                   | Governed metrics/semantic models, Power BI fix, supported JDBC/ODBC, and open multi-engine catalog access                                            |
| P1.5 Enterprise operations          | **Partial**               | Maintenance, leases, telemetry, backup/restore, verified eject, bounded connector resources, connector lifecycle operations, and safe errors                                                           | Connector UI, freshness/SLO dashboards, alerting, usage/cost reporting, and release runbooks                                                          |

## Shipped scope

### Managed definitions and administration

- [x] Tenant- and catalog-scoped connector definitions.
- [x] REST and gRPC source kinds with validated endpoints.
- [x] Data-product owner, description, tags, target, and quality policy.
- [x] Optimistic definition versions and conflict responses.
- [x] Active-run edit/delete protection.
- [x] Soft archival that retains definition, ownership, and run lineage.
- [x] Owner capability required on every connector administration endpoint.

### Durable execution and publication

- [x] Manual and interval-scheduled full-snapshot runs.
- [x] Atomic PostgreSQL claims with opaque lease generations.
- [x] Expired-run closure and stale-worker fencing.
- [x] PostgreSQL publication row lock held through DuckLake publication and durable completion.
- [x] Create-only first publication and exclusive connector target ownership, with a transactional
      table marker that makes an unconfirmed first publication safely recognizable on replay.
- [x] Atomic replacement after successful validation; preceding target retained on failure.
- [x] Scheduler claim losers re-query so another due connector is not starved.

### Source, quality, security, and resource controls

- [x] REST JSON-array and NDJSON reads with bearer-token environment references.
- [x] Server-streaming gRPC contract with bearer metadata and bounded receive messages.
- [x] HTTPS-by-default egress, optional host allowlist, DNS pinning, and private/unsafe destination
      refusal unless explicitly enabled.
- [x] Request timeout, response, snapshot, record, and row ceilings.
- [x] Shared node-level scratch concurrency, aggregate reservations, free-space floor, stale-file
      cleanup, and owner-only Unix permissions.
- [x] Minimum-row, required-column, and not-null quality gates evaluated before publication.
- [x] One aggregate DuckDB scan for row and not-null evidence.

### Lineage, API, telemetry, and verification

- [x] Durable trigger, worker, timing, rows read/published, nullable quality result, source version,
      and safe bounded error.
- [x] Partial row and source-version evidence retained when a read fails.
- [x] Truthful manual HTTP outcomes: success, conflict, quality, source/import, capacity, and
      published-but-unconfirmed states do not collapse into `200`.
- [x] Connector run count, duration, row, lease-conflict, and active-worker telemetry.
- [x] Real loopback REST and gRPC transport tests, PostgreSQL claim/fencing tests, migration tests,
      scratch tests, API-status tests, and domain-invariant tests.
- [x] Repository full-stack gate passed for this source delivery: backend, frontend, PostgreSQL/S3
      integration, container startup, private website, public/demo website, and production-operator
      journey.

## Connector-platform delivery and remaining work

### P1.1 release completion

- [x] Publish LakeHold v1.3.0 with the migration and connector runtime.
- [ ] Prove migration and a real connector refresh in a release deployment.

### P1.2 connector platform — shipped in v1.3.0

- [x] Public, versioned adapter manifest, read context/result, and bounded record-writer SDK
      contracts; built-in adapters use manifest version 1.
- [x] Durable incremental checkpoint and checkpoint-version state. A proposed checkpoint is run
      evidence but becomes current only inside the PostgreSQL publication fence after DuckLake
      commits.
- [x] At-least-once replay with atomic delete-and-insert keyed upsert. Replaying the same delta
      replaces the same business keys rather than duplicating them.
- [x] Per-connector exponential retry policy, pause, resume, immediate retry, terminal
      dead-letter status, and filtered dead-letter API.
- [x] Top-level field mappings with bounded `trim`, `lowercase`, `uppercase`, and `to-string`
      transforms; arbitrary code is not accepted.
- [x] Explicit `reject`, `additive`, and `mapped-version` schema policies. Mapped-version uses the
      declared field mapping as its contract and otherwise enforces reject compatibility.
- [x] Secret-provider abstraction with built-in `env://` compatibility and an HTTPS, DNS-pinned,
      bounded `vault://` provider, plus exact operator bindings across tenant, catalog, secret
      reference, and destination host.
- [x] Renewable OAuth refresh tokens, PKCS#12 mTLS client identity, bearer auth, PostgreSQL password
      auth, and allowlisted `X-Api-Key`/`Api-Key` custom headers.
- [x] Typed-cursor PostgreSQL incremental adapter (`int64`, `timestamptz`, UUID, or text) with
      parameterised predicates, bounded pages, unique cursors, explicit commit-monotonic source
      contracts, and unambiguous UTC timestamp checkpoints.
- [x] OAuth-backed HubSpot Contacts adapter with token renewal, adaptive time windows below the
      10,000-result search ceiling, late-index overlap, shared node pacing, `Retry-After` handling,
      and a durable fully-read window checkpoint.

P1.2 is shipped in the v1.3.0 application and container artifacts. The adapter SDK remains a
source/API contract inside `Lakehold.Api`, not a separately published NuGet package, and the built-in
production catalogue is intentionally only REST, gRPC, PostgreSQL, and HubSpot Contacts. No broad
partner ecosystem is claimed.

### P1.3 catalog and governance

- [ ] Stable governed identity across connector tables, imported tables, views, saved queries,
      shares, and downstream consumers.
- [ ] Searchable asset catalog and glossary.
- [ ] Column descriptions, classifications, sensitivity labels, and policy administration.
- [ ] Freshness objectives and visible current quality/freshness status.
- [ ] Navigable upstream/downstream lineage graph.
- [ ] Contract versions and compatibility decisions.
- [ ] Row- and column-level security.

### P1.4 semantic and consumption layer

- [ ] Governed metric definitions and reusable semantic models.
- [ ] Optional semantic-model generation from EF Core metadata.
- [ ] Power BI PostgreSQL type-catalogue compatibility fix.
- [ ] Supported JDBC/ODBC compatibility strategy.
- [ ] Read-only Iceberg REST or equivalent open multi-engine interoperability.
- [ ] Versioned client SDK experience beyond raw HTTP contracts.

### P1.5 enterprise operations

- [ ] Workbench connector administration and run-history experience.
- [x] Operator retry, pause, resume, dead-letter listing, and checkpoint inspection over the owner API.
- [ ] Freshness and connector service-level objectives.
- [ ] Alerts for repeated failures, stale data, lease contention, and capacity pressure.
- [ ] Per-connector resource and cost/usage reporting.
- [ ] Release, upgrade, rollback, and incident runbooks for managed ingestion.

## Priority acceptance gates

Priority 1 is complete only when all of these are demonstrable:

- **Acquire:** supported batch and incremental connectors resume after worker failure without lost
  checkpoints or duplicate publication.
- **Store and process:** publication remains atomic on source, quality, schema, cancellation, and
  node failure; large reads remain bounded and observable.
- **Govern:** every managed asset has stable identity, owner, description, classification, contract,
  freshness, quality, audit, and navigable upstream/downstream lineage.
- **Serve:** SQL, REST/client, Power BI, and at least one open multi-engine path consume the same
  governed assets and authorization decisions.
- **Operate:** operators inspect schedules, retries, leases, resource usage, alerts, and objectives
  without querying raw control-plane tables.
- **Secure:** credentials use an external secret provider, egress stays allowlisted and DNS-pinned,
  tenant boundaries are structural, and no secret or source row appears in logs or durable errors.

## Next implementation

Move to P1.3: give every governed asset a stable identity, searchable metadata, classifications,
freshness objectives, contract versions, and navigable upstream/downstream lineage. In parallel,
capture post-release migration and real connector-refresh evidence from a deployed v1.3.0
environment; the shipped status does not imply that operational proof.
