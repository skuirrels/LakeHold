# Enterprise Data Platform delivery plan

This is the delivery and status record for positioning LakeHold as a focused Enterprise Data
Platform (EDP), not merely a SQL endpoint over open storage.

**Status date:** 2 August 2026

**Status vocabulary:**

- **Implemented in source** — code and tests are committed in the repository, but no published
  release is claimed.
- **Shipped** — available on the main branch and in a released artifact.
- **Partial** — useful capability exists, but the stated EDP acceptance boundary is not met.
- **Not started** — no production implementation is claimed.

> The managed connector foundation is implemented, committed, and verified in source for the next
> release. It is not included in the current v1.2.0 artifact and must not be described as released
> until a later LakeHold version is published and independently verified.

## Executive status

| Workstream                          | Status                    | Completed boundary                                                                                                                                                                                    | Remaining boundary                                                                                                                                   |
| ----------------------------------- | ------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------- |
| P1.1 Managed ingestion foundation   | **Implemented in source** | REST JSON-array/NDJSON and gRPC full snapshots, durable definitions/runs, schedules, fencing, target ownership, quality, egress, scratch controls, telemetry, API outcomes, and production-path tests | Published release and post-release deployment evidence                                                                                               |
| P1.2 Connector platform             | **Not started**           | —                                                                                                                                                                                                     | Versioned SDK/manifest, checkpoints, incremental reads, retry/backoff, transforms, schema policy, secret providers, and first database/SaaS adapters |
| P1.3 Catalog and governance         | **Partial**               | Owners, descriptions, tags, quality policy, audit, and connector run lineage exist on initial surfaces                                                                                                | Stable identity for every asset, search, classification, freshness, policy administration, and end-to-end lineage graph                              |
| P1.4 Semantic and consumption layer | **Partial**               | HTTP, Workbench, MCP, EF Core, PostgreSQL wire for psql/DBeaver/Npgsql, and saved-query publication                                                                                                   | Governed metrics/semantic models, Power BI fix, supported JDBC/ODBC, and open multi-engine catalog access                                            |
| P1.5 Enterprise operations          | **Partial**               | Maintenance, leases, telemetry, backup/restore, verified eject, bounded connector resources, and safe errors                                                                                          | Connector UI, retry operations, freshness/SLO dashboards, alerting, usage/cost reporting, and release runbooks                                       |

## Completed in source

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
- [x] Create-only first publication and exclusive connector target ownership.
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

## Not completed

### P1.1 release completion

- [ ] Publish a versioned LakeHold artifact containing the migration and connector runtime.
- [ ] Prove migration and a real connector refresh in a release deployment.

### P1.2 connector platform

- [ ] Versioned adapter manifest and SDK.
- [ ] Durable incremental checkpoint contract that advances only after DuckLake commit.
- [ ] At-least-once incremental replay and idempotent publication model.
- [ ] Connector-specific retry, exponential backoff, pause, resume, and dead-letter operations.
- [ ] Field mappings and bounded transformation steps.
- [ ] Explicit schema behavior: reject, additive evolution, or mapped version.
- [ ] External secret-provider abstraction.
- [ ] OAuth token renewal, mTLS, and approved custom authentication mechanisms.
- [ ] PostgreSQL incremental connector.
- [ ] One OAuth-backed SaaS connector.

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
- [ ] Operator retry, pause, resume, and checkpoint inspection.
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

Build P1.2 around an adapter manifest and checkpoint contract. Prove it with one PostgreSQL
incremental connector and one OAuth-backed SaaS connector: together they exercise transactional
cursors and renewable credentials without prematurely creating a broad adapter catalogue.
