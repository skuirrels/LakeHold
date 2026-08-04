# Enterprise Data Platform delivery plan

This is the delivery and status record for positioning LakeHold as a focused Enterprise Data
Platform (EDP), not merely a SQL endpoint over open storage.

**Status date:** 3 August 2026

**Status vocabulary:**

- **Implemented in source** — code and tests are present and verified in repository source, but no
  published release is claimed.
- **Shipped** — available on the main branch and in a released artifact.
- **Partial** — useful capability exists, but the stated EDP acceptance boundary is not met.
- **Not started** — no production implementation is claimed.

> LakeHold v1.3.0 ships the managed connector foundation and connector platform, and v1.4.0 ships
> the versioned public API server. A real connector migration and refresh in a deployed environment
> remains an explicit post-release evidence gate; a release alone does not claim that operational proof.

## Executive status

| Workstream                          | Status                    | Completed boundary                                                                                                                                                                                    | Remaining boundary                                                                                                                                   |
| ----------------------------------- | ------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------- |
| P1.1 Managed ingestion foundation   | **Shipped**               | v1.3.0 includes REST JSON-array/NDJSON and gRPC full snapshots, durable definitions/runs, schedules, fencing, target ownership, quality, egress, scratch controls, telemetry, API outcomes, and production-path tests | Post-release migration and real connector-refresh deployment evidence                                                                                 |
| P1.2 Connector platform             | **Shipped**               | v1.3.0 includes the versioned adapter SDK/manifest, commit-fenced checkpoints, replay-safe keyed upsert, retry/dead-letter lifecycle, mappings, schema policy, external secrets, approved auth, PostgreSQL, and HubSpot | Deployment evidence and a broader production-certified adapter catalogue                                                                              |
| P1.3 Catalog and governance         | **Partial**               | Owners, descriptions, tags, quality policy, audit, and connector run lineage exist on initial surfaces                                                                                                | Stable identity for every asset, search, classification, freshness, policy administration, end-to-end lineage graph, and row/column-level security    |
| P1.4 Public API and client SDKs     | **Partial**               | v1.4.0 public API images; canonical `/api/v1`, NDJSON query/CDC streams, snapshot detail/keysets, production OpenAPI, semantic compatibility gate, generated/tested Java, Go, .NET, and Python clients, released-image authentication/query/isolation/cancellation conformance, documentation, examples, matrices, and a successful 0.1.0 non-publishing package/provenance dry run | Complete exhaustive public-error conformance; sign, publish, index, and clean-install all four public packages |
| P1.5 Semantic and consumption layer | **Partial**               | HTTP, Workbench, MCP, EF Core, PostgreSQL wire for psql/DBeaver/Npgsql, and saved-query publication                                                                                                   | Governed metrics/semantic models, Power BI fix, supported JDBC/ODBC, and open multi-engine catalog access                                            |
| P1.6 Enterprise operations          | **Partial**               | Maintenance, leases, telemetry, backup/restore, verified eject, bounded connector resources, connector lifecycle operations, and safe errors                                                           | Connector UI, freshness/SLO dashboards, alerting, usage/cost reporting, and release runbooks                                                          |

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
- [ ] Row- and column-level security — see below; it is not a checkbox.

#### Row- and column-level security

The most-requested governance gap, and the only P1.3 item that does not fit the architecture as it
stands. It is listed here rather than scheduled, because the design question below has to be settled
before any of it can be estimated honestly.

**The problem.** Invariants 4 and 20 say capability is expressed as *attachment*: a reader gets a
read-only catalog handle, so a write fails in the engine rather than in a policy check that clever
SQL might route around. Parsing, filtering, or rewriting submitted SQL is explicitly not the
security boundary. But a row is not attachable, and DuckDB has no native row policies and no in-process user
system to hang them on. So row-level security cannot be expressed the way every other capability in
LakeHold is — which is precisely why it has stayed unbuilt, and why "add a predicate to the query"
is the wrong first move.

**Candidate approaches**, none yet chosen:

- **Policy-bearing views, reachable by attachment.** Base tables live where the policy-bound
  credential cannot attach; it attaches only a catalog of views carrying the predicate. This is the
  one option that keeps the boundary at attachment. It requires the catalog topology to change, and
  the cross-catalog view resolution to be proven against DuckLake rather than assumed.
- **Filtering in a serving layer** above the engine. Viable only if raw SQL and the PostgreSQL wire
  endpoint are closed to policy-bound principals — otherwise the policy is advisory, and an advisory
  security control is worse than a declared absence.
- **Per-principal materialised subsets.** Correct and simple, but stale by construction, multiplies
  storage, and has no coherent answer for time travel.
- **Wait for upstream.** DuckDB or DuckLake may grow a primitive that makes this expressible. Worth
  tracking in `COMPETITIVE-RESEARCH.md` rather than designing around its absence twice.

**Acceptance gates**, whichever approach wins:

- [ ] A policy is enforced for a credential that can submit arbitrary SQL, including through the
      PostgreSQL wire endpoint and MCP — or those surfaces are provably closed to policy-bound
      principals, and say so.
- [ ] Enforcement survives a query that names the base table directly, a view over it, a join, a
      subquery, and a time-travel read at an older snapshot.
- [ ] The mechanism is stated in `ARCHITECTURE.md` as an invariant, or invariant 4 is amended
      explicitly. Shipping something that quietly contradicts it is the failure mode to avoid.
- [ ] `/compare`, the landing page, and `ARCHITECTURE.md` stop saying "no row policies" in the same
      change. All three assert it today, and the capability contract in
      `web/lakehold-ui/e2e/support/compare-capabilities.ts` will fail until the claim and its
      evidence move together.

### P1.4 public API and client SDKs

The SDKs are clients of the public HTTP API. They do not access PostgreSQL, DuckDB, DuckLake, the
internal LINQ-planner transport, or unversioned Workbench endpoints directly. The reviewed OpenAPI
contract is the shared source of truth; each language adds an idiomatic wrapper without copying
LakeHold business policy into four implementations.

#### Public API foundation

- [x] Implement a stable `/api/v1` surface and retain the current `/api` routes only as time-bounded,
      documented compatibility aliases.
- [x] Publish OpenAPI in every supported environment, with stable operation identifiers, schemas,
      examples, security requirements, and documented compatibility policy.
- [x] Return RFC 9457 `application/problem+json` errors with stable LakeHold error codes and request
      correlation identifiers.
- [x] Apply cursor pagination to bounded list routes, `Idempotency-Key` to bounded retryable
      mutations whose responses contain no one-time credential, and a durable
      operation resource to long-running work.
- [x] Bound coordination-state growth: retain completed idempotency responses for seven days and
      terminal durable-operation records for 30 days, while never automatically deleting
      in-progress or running records.
- [ ] Replace the generic protected-offset list cursor with source-native keyset or snapshot cursors
      everywhere concurrent collection changes or deep traversal require stable scale. Snapshot
      history now has a frozen native snapshot-id keyset and CDC retains its native snapshot/row
      cursor; the remaining generic control-plane lists still use protected offsets.
- [x] Expose capability discovery so a client can negotiate server/API versions and optional
      features instead of guessing from a LakeHold release number.
- [x] Version the existing control surface for access, tenants, catalogs, tokens, schema, bounded
      queries, saved queries, connectors and runs, dead letters, checkpoints, snapshots, CDC,
      maintenance, backups, ejects, operations, and audit history.
- [x] Add NDJSON query and CDC streaming, snapshot filters/detail, and bounded table preview at an
      exact retained snapshot. Full arbitrary-query `asOf`, labels/pins, and catalog-wide restore
      remain separate time-travel phases and are not implied by this item.
- [x] Keep public DTOs independent of EF/control-plane entities and prevent secrets, connection
      material, stack traces, operation implementation paths, or source rows from entering responses
      or durable errors. Operator-supplied storage locations remain explicit provisioning inputs.
- [x] Add frozen-contract validation, unique-operation, security, endpoint-convention, generated-SDK
      drift, and SDK build/test gates to CI.
- [x] Add an automated semantic OpenAPI compatibility diff against the merge base. Breaking changes
      require a new API major version; additive changes remain backward compatible.

The detailed endpoint contract and staged migration remain in
[`PUBLIC-API.md`](PUBLIC-API.md).

#### Supported SDKs

- [x] Generate and test a Java source package with typed models, Bearer authentication, and
      synchronous/asynchronous low-level operations.
- [x] Generate and test a Go source module with typed models, Bearer authentication, and
      context-aware low-level operations.
- [x] Generate and test a .NET source package with typed models, Bearer authentication, and
      cancellable low-level async operations. This is separate from the replication-only
      `src/Lakehold.Client` project.
- [x] Generate and test a typed Python source package with Bearer authentication and synchronous
      low-level operations.
- [x] Give all SDKs the same authentication, typed problem errors, bounded retries with
      `Retry-After`, idempotency, cursor iteration, operation polling, explicit request timeouts,
      user-agent/version, correlation-id behavior, and additive-field tolerance. Go and .NET
      propagate request cancellation, Java supports generated-call cancellation and interruptible
      waits, and synchronous Python provides cooperative cancellation between retries/polls while
      the timeout bounds an in-flight call.
- [x] Add streaming query and CDC consumption without materialising an unbounded response, with the
      same cancellation and error behavior in every SDK.
- [x] Generate transport models and low-level operations from the reviewed OpenAPI document, then
      keep handwritten convenience layers small and language-idiomatic. Do not hand-maintain four
      divergent copies of the wire contract.
- [x] Add equivalent per-language conformance tests for Bearer authentication and shared response
      deserialization.
- [x] Run the shared language-neutral reliability fixture through all four source SDKs for typed
      problems, pagination, retries, idempotency, operation polling, transport-appropriate cancellation, request ids,
      timeouts, user agents, token redaction, and unknown additive fields.
- [x] Run authenticated query-streaming, tenant-isolation, and streaming-cancellation conformance
      through all four SDKs against an immutable released API image. **Evidence is dated, not
      standing:** `sdk-conformance.yml` runs on a weekly schedule and on manual dispatch against a
      pinned image tag, so this box records the most recent run rather than every commit. It is not
      a pull-request gate — the released image it tests is by definition not the merge candidate.
      Record the image tag and date when citing this as release evidence.
- [ ] Extend released-image conformance to every stable public error code.
- [x] Publish source reference documentation, runnable examples, a supported runtime matrix,
      compatibility policy, changelog, provenance generation, and coordinated fail-closed release
      automation.
- [ ] Publish the signed packages through that workflow and prove registry indexing plus clean
      installs. No Maven Central, Go proxy, NuGet, or PyPI publication is claimed by source automation.

P1.4 remains **partial**. Its server contract ships in v1.4.0, but the workstream is complete only
when all four packages are publicly installable, pass the complete conformance fixtures against a
released LakeHold server, and no supported workflow requires an internal route.
The existing `src/Lakehold.Client` project is useful implementation evidence, but it is not currently
a published general-purpose SDK and must not be presented as one.

### P1.5 semantic and consumption layer

- [ ] Governed metric definitions and reusable semantic models.
- [ ] Optional semantic-model generation from EF Core metadata.
- [ ] Power BI PostgreSQL type-catalogue compatibility fix.
- [ ] Supported JDBC/ODBC compatibility strategy.
- [ ] Read-only Iceberg REST or equivalent open multi-engine interoperability.
- [ ] Integrate governed semantic models with the P1.4 public API and SDKs without inventing a
      separate authorization or identity model.

### P1.6 enterprise operations

- [ ] Workbench connector administration and run-history experience.
- [x] Operator retry, pause, resume, dead-letter listing, and checkpoint inspection over the owner API.
- [ ] Freshness and connector service-level objectives.
- [ ] Alerts for repeated failures, stale data, lease contention, and capacity pressure.
- [ ] Per-connector resource and cost/usage reporting.
- [ ] Release, upgrade, rollback, and incident runbooks for managed ingestion.

## Priority acceptance gates

These are split into two tiers. A single list meant P1 could only close when every remaining
workstream closed — the *Serve* gate alone requires Power BI and open multi-engine access, both of
which are unstarted P1.5 items — which made "Priority 1" indistinguishable from "the whole roadmap".
The tiers let the ingestion half be signed off on its own evidence.

### P1a — ingestion and safety

Demonstrable independently of governance and consumption work:

- **Acquire:** supported batch and incremental connectors resume after worker failure without lost
  checkpoints or duplicate publication.
- **Store and process:** publication remains atomic on source, quality, schema, cancellation, and
  node failure; large reads remain bounded and observable.
- **Secure:** credentials use an external secret provider, egress stays allowlisted and DNS-pinned,
  tenant boundaries are structural, and no source credential or source row appears in logs or
  durable errors. The first-start instance bootstrap token remains the documented one-time operator
  log exception unless it is injected through `Lakehold__BootstrapToken`.

P1a is met in source and shipped in v1.3.0. It closes when post-release migration and a real
connector refresh are proven in a deployed environment.

### P1b — governance and consumption

Each depends on work that is currently unstarted, and none may be claimed early:

- **Govern:** every managed asset has stable identity, owner, description, classification, contract,
  freshness, quality, audit, and navigable upstream/downstream lineage.
- **Serve:** SQL, the versioned public API and all four supported SDKs, Power BI, and at least one
  open multi-engine path consume the same governed assets and authorization decisions.
- **Operate:** operators inspect schedules, retries, leases, resource usage, alerts, and objectives
  without querying raw control-plane tables.

## Next implementation

The P1.4 server contract ships in v1.4.0 and its four source SDKs pass released-image authentication,
query-streaming, tenant-isolation, and cancellation conformance. Continue in this order:

### Future SDK registry publication

The SDK source packages exist, but none of the following public distribution tasks is complete.
Treat every registry as future work until its package is independently indexed and a clean consumer
can install the exact published version:

- **Maven Central:** publish `io.lakehold:lakehold-sdk:0.1.0` with protected Central credentials,
  namespace ownership, GPG signing, provenance, indexing, and a clean Maven consumer build.
- **Go module proxy:** publish the immutable signed tag for
  `github.com/skuirrels/LakeHold/sdk/go`, verify proxy and checksum-database indexing, and run a clean
  downstream module build.
- **NuGet:** publish `Lakehold.Sdk` version `0.1.0` with protected registry credentials, package
  signing and provenance, verify public indexing, and run a clean restore and consumer build.
- **PyPI:** publish `lakehold-sdk` version `0.1.0` through trusted publishing with attestations,
  verify public indexing, and install it in a clean virtual environment.

Configure protected release environments and registry ownership first. With explicit registry
approval, run `sdk-release.yml` with publication enabled and verify every registry independently;
the successful non-publishing dry run is not publication evidence. Only then mark P1.4 shipped.

After that publication work:

1. Extend the shared released-image fixtures to every stable public error code.
2. Continue the remaining time-travel resources: arbitrary-query `asOf`, labels/pins/retention, and
   catalog-wide restore. Continue converting generic offset cursors only where source semantics make
   a stable keyset possible.

P1.3 governance proceeds through the same contract: stable governed asset identifiers must land
before SDK models for assets and lineage are frozen. In parallel, capture post-release migration and
real connector-refresh evidence from a deployed released environment; the shipped status does not
imply that operational proof.
