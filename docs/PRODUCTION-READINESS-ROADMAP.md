# Lakehold production-readiness roadmap

## Readiness conclusion

Lakehold has a strong technical foundation: a clear control-plane/data-plane split, typed APIs,
cancellable streaming queries, bounded materialised results, dry-run destructive maintenance,
verified backup/eject manifests, structured telemetry, real-engine integration tests, and non-root
production images with health checks and resource limits.

It is suitable for trusted development and evaluation today. It should not be represented or
deployed as a secure shared multi-tenant service until the isolation and production-security gates
below are complete. New product features should not take priority over Phases 1–3.

## Phase 1 — Make tenant isolation a proved invariant

**Priority:** Release blocker.

Every persisted artifact and every piece of node-local state must be keyed by tenant identity as
well as catalog name. Two tenants are currently allowed to use the same catalog name, so catalog
name alone cannot identify storage or a warm session.

### Actions

- Define one canonical tenant/catalog identity and carry it from the control-plane resolution into
  the engine descriptor.
- Derive default metadata and data locations from both tenant and catalog. Prevent
  `alpha/analytics` and `beta/analytics` from resolving to the same DuckLake metadata file or
  Parquet root.
- Key warm sessions by tenant, catalog, and attachment mode. Make eviction and inspection use the
  same full key so changing or deleting one tenant's catalog cannot affect another tenant's
  session.
- Namespace backup generations, restore discovery, and eject bundles by tenant and catalog.
- Review every catalog-name-only cache, path, lease, subscription, audit, and maintenance lookup;
  either make it tenant-aware or document why it is intentionally global.
- Define an upgrade path for existing catalog-name-only storage. During transition, fail closed on
  ambiguous layouts instead of attaching whichever path already exists.
- Keep read-only and read-write attachments separate within the tenant-aware key.

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

**Priority:** Release blocker.

### Actions

- **Partly landed:** production Compose enables authentication by default. Remaining work is to limit
  token-less legacy mode to an explicit development setting and refuse production start-up when it
  is enabled accidentally.
- **Landed:** `/api/maintenance/schedule` carries the authorization filter, requires `Listing`, and
  projects only the tenant and catalog runs reachable by the credential.
- Review all routes outside the authenticated tenant group and add a test that inventories the
  intended public endpoints.
- Add authentication-attempt rate limiting and document TLS termination requirements.

### Exit gate

A default production deployment exposes no tenant, query, provisioning, maintenance, backup, or
operational data without a valid credential, while the documented development flow remains easy to
run deliberately.

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
Lakehold's control-plane/runtime files, even when paths and catalog names are known.

## Phase 4 — Secure webhook delivery

### Actions

- Give subscription creation, mutation, and deletion an explicit capability; do not grant
  persistent outbound integration management merely because a principal can read data.
- Default to HTTPS and support an operator-controlled destination allowlist.
- Block loopback, link-local, private, metadata-service, and otherwise prohibited destinations,
  including after DNS resolution and redirects. Revalidate on every delivery to address DNS
  rebinding.
- Apply egress network policy outside the application as a second boundary.
- Bound redirects, response headers, connection lifetime, payload size, retry volume, and total
  concurrent delivery work.

### Exit gate

Webhook tests cover private-address targets, redirects, DNS changes, authorization roles, timeout
and backoff behavior, and prove that signing secrets and response bodies do not enter logs or APIs.

## Phase 5 — Make recovery and HA operationally credible

### Actions

- Make metadata, data, backup, and eject roots genuinely configurable; do not overwrite explicitly
  bound values while resolving defaults from the state root.
- Keep recoverable backups and eject bundles outside the primary host/volume failure domain.
- Add backup and restore for the control plane, including tenants, catalog descriptors, tokens,
  subscriptions, and required audit state.
- Publish and automate a restore drill that rebuilds a fresh deployment and independently verifies
  tenant-scoped row counts and manifests.
- Decide the supported topology explicitly: single-node, active/passive, or multi-node. If HA is
  supported, implement a shared control-plane store and verify cache invalidation, token revocation,
  CDC dispatch, and maintenance leasing across nodes.
- Define retention, encryption-at-rest, secret rotation, recovery-point, and recovery-time targets.

### Exit gate

A tested runbook can recover a fresh node after total loss of the primary volume without
cross-tenant ambiguity or undocumented manual database edits.

## Phase 6 — Enforce quality gates in CI

### Actions

- **Partly landed:** CI restores and tests the backend. Remaining gates are explicit build,
  format/style, and dedicated integration lanes.
- **Partly landed:** CI runs `npm ci`, the frontend unit suite, and the production build. Remaining
  gates are lint/format and a small end-to-end workbench smoke suite.
- Add dedicated PostgreSQL metadata, object-store, PostgreSQL-wire, and two-tenant isolation lanes.
- Add dependency vulnerability review, secret scanning, container build/scanning, and reproducible
  lockfile checks.
- Split the largest implementation hotspots as behavior changes require it; avoid a speculative
  rewrite, but keep protocol, authorization, orchestration, and persistence concerns independently
  testable.

### Exit gate

No pull request can merge without the production builds and security-critical isolation tests
passing. Required checks are branch-protected and produce retained diagnostic output on failure.

## Phase 7 — Adopt versioned control-plane migrations

### Actions

- Replace `EnsureCreated` plus open-ended additive repair as the long-term upgrade mechanism with a
  versioned, ordered migration history.
- Define supported forward-upgrade, restart-after-interruption, backup-before-migration, and rollback
  behavior.
- Add fixtures from every released schema version and test upgrades with representative existing
  data, constraints, token roles, and audit records.
- Keep the additive repair path only as a bounded compatibility bridge, with an explicit removal
  plan.

### Exit gate

Every supported historical schema upgrades deterministically on a copy of production-like state,
and a failed migration cannot silently leave a partially upgraded control plane.

## Phase 8 — Reconcile documentation and claims

### Actions

- Update the README's authentication, provisioning, provider-version, deployment, and status
  sections from the runtime source of truth.
- Describe isolation as implemented and tested, not intended. Remove the shared multi-tenant claim
  until the Phase 1 and Phase 3 gates pass.
- Distinguish the one-node production profile from any future HA/multi-node profile.
- Make configuration examples executable and add checks that important documented keys are actually
  honored.
- Keep architecture, authentication, exit-path, PostgreSQL-wire, public-API, and agent guidance
  documents aligned through review ownership or automated consistency checks.

### Final readiness gate

Call Lakehold production-ready for shared multi-tenancy only after Phases 1–6 are complete and a
release candidate passes an adversarial isolation review and a clean-machine recovery drill.
Phases 7–8 should complete before promising durable upgrades and publishing broad production
guidance.
