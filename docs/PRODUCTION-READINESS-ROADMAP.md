# LakeHold production-readiness roadmap

**Status reviewed:** 10 August 2026

## Readiness conclusion

LakeHold has a strong technical foundation: a clear control-plane/data-plane split, typed APIs,
cancellable streaming queries, bounded materialised results, dry-run destructive maintenance,
verified backup/eject manifests, structured telemetry, real-engine integration tests, and non-root
production images with health checks and resource limits.

It is suitable for a single tenant, trusted internal users, development, and evaluation today. It
should not be represented or deployed as a secure shared service for mutually untrusted tenants
until the arbitrary-SQL containment and production-security gates below are complete. Tenant-
qualified storage/session identity and unconditional authentication have landed; Phase 3 is now the
decisive isolation blocker. New product features should not take priority over that boundary,
aggregate workload admission, or a clean-machine recovery drill.

## Phase 1 — Make tenant-qualified identity and routing a proved invariant

**Implementation status:** Substantially landed; focused end-to-end proof and legacy hardening remain.

The current control-plane path carries tenant and catalog identity into engine descriptors. Default
data, backup, and eject locations are tenant-qualified; warm sessions include tenant, catalog id and
name, configuration version, and attachment mode; maintenance leases are tenant/catalog qualified.
Two tenants may use the same catalog name without sharing those identities. The remaining work is to
remove or isolate the engine's legacy blank-tenant fallback and prove the full lifecycle in one
production-path test.

### Actions

- **Landed:** define one canonical tenant/catalog identity and carry it from control-plane resolution into
  the engine descriptor.
- **Landed:** derive default metadata and data locations from both tenant and catalog. Prevent
  `alpha/analytics` and `beta/analytics` from resolving to the same DuckLake metadata file or
  Parquet root.
- **Landed:** key warm sessions by tenant, catalog identity, configuration version, and attachment
  mode. Eviction uses the same tenant/catalog identity.
- **Landed:** namespace backup generations, restore discovery, eject bundles, and maintenance leases
  by tenant and catalog.
- **Substantially landed:** current control-plane caches, paths, connector/subscription state, audit,
  and maintenance resolution use tenant or durable catalog identity. Keep this in the review
  checklist as new surfaces are added.
- **Open hardening:** define an upgrade path for existing catalog-name-only storage and remove the
  normal runtime's blank-tenant descriptor fallback. During transition, fail closed on
  ambiguous layouts instead of attaching whichever path already exists.
- **Landed:** keep read-only and read-write attachments separate within the tenant-aware key.

### Success criteria

- Two tenants can create the same catalog name and receive distinct metadata, data, backup, and
  eject locations.
- Cold and warm queries for those catalogs always reach the correct tenant's data, including under
  concurrent first access.
- Read-only and read-write sessions never cross either tenant or capability boundaries.
- Evicting, deleting, backing up, restoring, or ejecting one catalog has no observable effect on
  the other tenant's same-named catalog.
- No API response, scheduled job, audit record, or artifact listing exposes another tenant's
  state.

### Suggested verification

- Add an end-to-end two-tenant test using identical catalog names and distinguishable sentinel
  rows. Exercise alternating and concurrent HTTP and PostgreSQL-wire queries across cold starts,
  cache hits, read-only sessions, and eviction.
- Assert physical paths and manifests are tenant-qualified and distinct.
- Back up and eject both catalogs, then independently inspect each bundle and restore it into a
  fresh target; row counts and sentinel values must remain tenant-specific.
- Add negative tests for cross-tenant catalog guessing, cache reuse, artifact enumeration,
  maintenance, and deletion.
- Run the complete backend suite plus the real PostgreSQL and object-store integration lanes.

## Phase 2 — Fail closed in production

**Implementation status:** Core release blocker closed; rate limiting and route-inventory hardening remain.

### Actions

- **Landed:** authentication is unconditional. The former `RequireAuthentication` switch and
  token-less legacy mode no longer exist. Optional anonymous access is a named `DemoTenant` plus
  `DemoCatalog` reader identity and fails closed when either value is absent.
- **Landed:** `/api/maintenance/schedule` carries the authorization filter, requires `Listing`, and
  projects only the tenant and catalog runs reachable by the credential.
- **Open hardening:** review all routes outside the authenticated tenant group and add a test that inventories the
  intended public endpoints.
- **Open hardening:** add authentication-attempt rate limiting. TLS requirements for OIDC, tokens on
  PostgreSQL wire, and external connections are documented.

### Exit gate

A default production deployment exposes no tenant, query, provisioning, maintenance, backup, or
operational data without a valid credential, while the documented development flow remains easy to
run deliberately.

**Core exit-gate status:** Met in source and current authentication tests. Rate limiting remains a
separate abuse-control task rather than an authentication-enforcement switch.

## Phase 3 — Contain arbitrary SQL

**Priority:** Release blocker for untrusted tenants.

Attachment selection is not by itself a sandbox when callers can submit arbitrary DuckDB SQL.
Containment must cover new attachments, file and object-store readers, writes, secrets, extensions,
and outbound network access without relying on a fragile SQL keyword parser.

### Actions

- Write a threat model for the SQL surface, including `ATTACH`, `COPY`, `read_parquet`, `glob`,
  secret creation, extension installation/loading, local paths, remote URLs, and resource abuse.
- Choose and document the trust boundary. Prefer a per-tenant worker/process or container with only
  that tenant's mounts, storage credentials, network policy, CPU, memory, and temporary space.
- Apply DuckDB external-access restrictions where compatible with the chosen storage model, and
  lock configuration after trusted session setup.
- Separate operator-controlled maintenance SQL from tenant-submitted SQL capabilities.
- Add aggregate admission control so per-session memory and thread limits cannot collectively
  exhaust a node.

### Exit gate

Adversarial SQL cannot read, attach, alter, overwrite, or transmit another tenant's data or
LakeHold's control-plane/runtime files, even when paths and catalog names are known.

## Phase 4 — Secure webhook delivery

**Implementation status:** Application controls substantially landed; network enforcement and
address-classification hardening remain.

### Actions

- **Landed:** subscription creation, mutation, and deletion require `TenantWrite`; reading data alone
  does not grant persistent outbound-integration management.
- **Landed:** default to HTTPS and support an operator-controlled destination allowlist.
- **Substantially landed:** block loopback, link-local, private, metadata-service, multicast, CGNAT,
  and unique-local destinations,
  including after DNS resolution and redirects. Revalidate on every delivery to address DNS
  rebinding. Connections are pinned to the approved address and redirects are disabled. Complete
  IANA special-use and NAT64 classification remains open.
- **Deployment gate:** apply egress network policy outside the application as a second boundary.
- **Landed:** disable redirects; bound connection lifetime, payload size, retry/backoff volume, and total
  concurrent delivery work.

### Exit gate

Webhook tests cover private-address targets, host allowlists, address pinning, authorization roles,
timeouts, backoff, concurrency, stable signed payloads, and omission of response bodies from durable
errors. Add NAT64/special-use cases and retain an infrastructure egress-policy test before treating
this gate as complete in an untrusted network.

## Phase 5 — Make recovery and HA operationally credible

### Actions

- **Operational documentation landed:** [`OPERATIONS.md`](OPERATIONS.md) and the linked
  [incident-response](runbooks/INCIDENT-RESPONSE.md),
  [disaster-recovery](runbooks/DISASTER-RECOVERY.md), and
  [monitoring/alerting](runbooks/MONITORING-AND-ALERTING.md) runbooks define current procedures,
  explicit limitations, alert gates, and drill evidence. The implementation items below remain
  readiness work; documentation does not make a same-volume backup off-host or add control-plane
  restore support.
- **Landed:** metadata, data, backup, and eject roots are configurable and explicitly bound values are
  preserved while relative defaults resolve from the state root.
- Keep recoverable backups and eject bundles outside the primary host/volume failure domain.
- Add backup and restore for the control plane, including tenants, catalog descriptors, tokens,
  subscriptions, and required audit state.
- Publish and automate a restore drill that rebuilds a fresh deployment and independently verifies
  tenant-scoped row counts and manifests.
- **Landed:** PostgreSQL is the required shared control-plane store; new DuckLake catalogs also use
  isolated PostgreSQL schemas. Catalog resolution re-reads shared state, warm sessions include
  tenant/catalog/configuration identity, and migrations plus maintenance use PostgreSQL advisory
  locks/leases.
- Complete multi-node verification for token revocation, CDC dispatch, artifact naming, and
  failover. Each query remains worker-local; distributed SQL is not in scope.
- Define retention, encryption-at-rest, secret rotation, recovery-point, and recovery-time targets.

### Exit gate

A tested runbook can recover a fresh node after total loss of the primary volume without
cross-tenant ambiguity or undocumented manual database edits.

## Phase 6 — Enforce quality gates in CI

**Implementation status:** Functional and production-path coverage is broad; adversarial isolation
and supply-chain security gates remain.

### Actions

- **Landed:** CI restores, builds, and tests the backend and verifies Kafka Avro through trusted
  proxy gateways.
- **Landed:** CI runs `npm ci`, frontend unit tests, the production build, real Chromium journeys,
  and a destructive disposable production-operator simulation. Explicit lint/Prettier enforcement
  remains open.
- **Landed in dedicated or real-client coverage:** PostgreSQL metadata and S3 integration run with
  skipped-test detection; PostgreSQL wire is exercised through Npgsql and socket-level tests.
  A production-path adversarial two-tenant SQL-containment lane remains open.
- Add dependency vulnerability review, secret scanning, container build/scanning, and reproducible
  lockfile checks.
- Split the largest implementation hotspots as behavior changes require it; avoid a speculative
  rewrite, but keep protocol, authorization, orchestration, and persistence concerns independently
  testable.

### Exit gate

No pull request can merge without the production builds and security-critical isolation tests
passing. Required checks are branch-protected and produce retained diagnostic output on failure.

## Phase 7 — Adopt versioned control-plane migrations

**Implementation status:** Versioned production migrations landed; lifecycle evidence remains.

### Actions

- **Landed:** the production PostgreSQL control plane runs a versioned, ordered EF migration history
  under a PostgreSQL advisory lock. `EnsureCreated` is confined to tests. The narrow additive adapter
  remains only for copying legacy DuckDB control-plane files into a migration-managed PostgreSQL
  target.
- Define supported forward-upgrade, restart-after-interruption, backup-before-migration, and rollback
  behavior.
- Add fixtures from every released schema version and test upgrades with representative existing
  data, constraints, token roles, and audit records.
- Keep the legacy import adapter as a bounded compatibility bridge, with an explicit support and
  removal plan.

### Exit gate

Every supported historical schema upgrades deterministically on a copy of production-like state,
and a failed migration cannot silently leave a partially upgraded control plane.

## Phase 8 — Reconcile documentation and claims

**Implementation status:** Repository-wide reconciliation performed 10 August 2026; automated drift
prevention remains.

### Actions

- **Landed in the 10 August reconciliation:** update authentication, tenant qualification,
  migrations, CI, deployment-support, and status claims from the runtime source of truth.
- **Landed in documentation:** distinguish credential-bound tenant/catalog routing from the still-open
  arbitrary-SQL process/filesystem/network boundary. Do not claim secure shared untrusted tenancy
  until Phase 3 passes.
- **Landed in documentation:** distinguish the supported Compose/single-node profile, worker-local
  multi-node behaviour, and container portability from a supported Kubernetes profile.
- Make configuration examples executable and add checks that important documented keys are actually
  honored.
- Keep architecture, authentication, exit-path, PostgreSQL-wire, public-API, and agent guidance
  documents aligned through review ownership or automated consistency checks.

### Final readiness gate

Call LakeHold production-ready for shared, mutually untrusted multi-tenancy only after the Phase 1
proof is complete, Phase 3 containment plus aggregate admission control has landed, and a release
candidate passes adversarial isolation, supply-chain, and clean-machine recovery gates. Versioned
migrations have landed, but released-version upgrade fixtures and automated documentation-drift
checks remain necessary before promising a broad long-term support lifecycle.
