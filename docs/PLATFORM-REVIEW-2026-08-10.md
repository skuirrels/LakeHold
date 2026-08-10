# LakeHold Platform Review

**Review date:** 10 August 2026
**Scope:** API, control plane, query engine, DuckDB/DuckLake isolation, PostgreSQL wire endpoint, managed connectors, Workbench, SDK and public API delivery, deployment, security, operations, documentation, and automated verification.
**Review mode:** Read-only repository and runtime inspection. No product source was changed by the review.

## Documentation follow-up

The documentation reconciliation requested from this review was completed on 10 August 2026.
LH-016 is corrected in the repository documentation and stale source comments. LH-017's deployment
claim is corrected to distinguish container portability from a supported Kubernetes profile; the
absence of maintained Kubernetes artifacts remains an open product/operations gap. No other finding
in this report is claimed fixed by the documentation-only follow-up.

## Executive assessment

LakeHold has a coherent architecture, strong authentication foundations, broad automated coverage, and unusually explicit documentation of its limitations. It is credible today for a single tenant or a trusted internal user population with appropriate operational controls.

It is **not yet safe for mutually untrusted tenants sharing one query node**. The decisive release blocker is that credentials and attachments constrain tenant/catalog routing and writes through the selected catalog, but do not contain tenant-submitted DuckDB SQL at the process, filesystem, credential, or network boundary. A read-only catalog attachment does not prevent direct file readers, new attachments, external URLs, secret functions, or other DuckDB capabilities from reaching resources visible to the shared process.

The recommended product posture is therefore:

- Treat shared untrusted multi-tenancy as unsupported until the P0 containment gate is complete.
- Continue to support single-tenant and trusted-team deployments with explicit operational guidance.
- Correct PostgreSQL-wire transaction semantics before describing the endpoint as writable PostgreSQL compatibility.
- Prioritise containment, correctness, admission control, and recovery ahead of additional product breadth or broad refactoring.

## Severity model

| Priority | Meaning |
|---|---|
| **P0 Critical** | Release blocker or plausible cross-tenant/security-boundary failure |
| **P1 High** | Serious correctness, availability, data-fidelity, or recovery risk |
| **P2 Medium** | Material security hardening, operational reliability, or governance gap |
| **P3 Low** | Maintainability, documentation, or defence-in-depth issue with limited immediate impact |

## Findings summary

| ID | Priority | Finding | Classification |
|---|---|---|---|
| LH-001 | **P0 Critical** | Tenant SQL is not contained at the filesystem, network, or process boundary | Confirmed release blocker |
| LH-002 | **P1 High** | PostgreSQL wire acknowledges transactions that are never executed | Confirmed correctness defect |
| LH-003 | **P1 High** | Shared PostgreSQL-wire passwords allow tenant impersonation | Confirmed unsafe configuration mode |
| LH-004 | **P1 High** | Per-session limits do not provide aggregate node admission control | Confirmed resource-isolation gap |
| LH-005 | **P1 High** | PostgreSQL wire can fail to bind while readiness remains healthy | Confirmed availability defect |
| LH-006 | **P1 High** | Incremental connectors do not represent source deletions | Confirmed data-fidelity gap |
| LH-007 | **P1 High** | Control-plane recovery is not automated or proven end to end | Confirmed production-readiness gap |
| LH-008 | **P2 Medium** | Outbound destination policy does not fully cover NAT64 and special-use networks | Defence-in-depth gap |
| LH-009 | **P2 Medium** | API and wire authentication/resource throttling is incomplete | Confirmed abuse-control gap |
| LH-010 | **P2 Medium** | Core engine and wire options lack complete startup validation | Confirmed configuration gap |
| LH-011 | **P2 Medium** | Release supply-chain controls are incomplete | Confirmed delivery gap |
| LH-012 | **P2 Medium** | Workbench edge security and OIDC callback logging need hardening | Confirmed web-security gap |
| LH-013 | **P2 Medium** | Bootstrap-token delivery through logs expands credential exposure | Intentional design with production risk |
| LH-014 | **P2 Medium** | API container hardening is weaker than the LINQ compiler sandbox | Confirmed deployment gap |
| LH-015 | **P2 Medium** | Security-critical adversarial tests do not prove the real isolation boundary | Confirmed verification gap |
| LH-016 | **P2 Medium** | Readiness and operations documentation has drifted from the implementation | Corrected by documentation follow-up |
| LH-017 | **P2 Medium** | Kubernetes deployment claims are not backed by supported deployment artifacts | Claim corrected; support gap remains |
| LH-018 | **P2 Medium** | Governance, semantic, BI, interoperability, and SDK delivery remain partial | Product gap group |
| LH-019 | **P3 Low** | Tenant identity is not a mandatory engine descriptor invariant | Latent isolation footgun |
| LH-020 | **P3 Low** | Query errors are returned verbatim to authenticated callers | Information-disclosure trade-off |
| LH-021 | **P3 Low** | Large implementation hotspots increase coupling and review risk | Architecture/maintainability gap |

## Detailed findings

### LH-001 — Tenant SQL is not contained at the filesystem, network, or process boundary

**Priority:** P0 Critical
**Status:** Confirmed in source and acknowledged by the existing production-readiness roadmap.

#### Evidence

- Tenant query routes accept SQL and pass it through `QueryExecutionCoordinator` to a `Duckling` session:
  - [`src/Lakehold.Api/Endpoints/LakehouseEndpoints.cs`](../src/Lakehold.Api/Endpoints/LakehouseEndpoints.cs), `ExecuteAsync`
  - [`src/Lakehold.Engine/Execution/Duckling.cs`](../src/Lakehold.Engine/Execution/Duckling.cs), `SqlQueryDynamicRawAsync`
- A reader attaches the selected DuckLake catalog read-only, but this constrains the attachment rather than the whole DuckDB connection.
- Session creation loads `ducklake`, `httpfs`, `json`, `parquet`, `excel`, and `avro`, applies a per-session memory limit and thread count, and then executes raw SQL.
- [`src/Lakehold.Api/Storage/DucklingSessionConfigurator.cs`](../src/Lakehold.Api/Storage/DucklingSessionConfigurator.cs) creates scoped storage and metadata secrets, but does not disable external access, restrict directories, or lock DuckDB configuration after trusted setup.
- [`compose.production.yaml`](../compose.production.yaml) mounts every local tenant's state beneath the shared `/var/lib/lakehold` volume.
- [`src/Lakehold.ControlPlane/Data/QueryPlanValidator.cs`](../src/Lakehold.ControlPlane/Data/QueryPlanValidator.cs) rejects external-access functions only for externally generated plans. It is not a sandbox for direct SQL submitted through the normal query endpoint.
- [`PRODUCTION-READINESS-ROADMAP.md`](PRODUCTION-READINESS-ROADMAP.md#phase-3--contain-arbitrary-sql) already classifies this as a release blocker.

#### Impact

A tenant that can submit SQL may be able to use capabilities such as `glob`, `read_parquet`, `read_csv`, `ATTACH`, `COPY`, external URLs, secret inspection/selection functions, or extension functions against resources visible to the shared process. For local storage, knowing or discovering another tenant's physical path can bypass the catalog attachment entirely. Read-only catalog mode does not prevent direct reads from files outside that catalog.

Per-session storage-secret scoping is good defence in depth, but it is not a substitute for a process boundary. The same concern applies to runtime files, temporary files, object-store credentials, outbound network access, and resource abuse.

#### Required remediation

1. Execute untrusted SQL in a tenant-specific worker process or container.
2. Mount only that tenant's data, temporary directory, and required read-only runtime assets.
3. Give the worker only tenant-scoped storage credentials and a controlled egress policy.
4. Complete trusted extension, secret, and catalog setup before restricting DuckDB external access and locking configuration.
5. Separate operator maintenance SQL from tenant-submitted query capability.
6. Apply node and tenant CPU, memory, PID, temporary-space, and wall-clock bounds outside DuckDB as well as inside it.
7. Do not use a SQL keyword parser as the isolation boundary.

#### Exit gate

Adversarial tests must prove that a tenant cannot read, list, attach, alter, overwrite, or transmit another tenant's data or any LakeHold control-plane/runtime file, even when exact paths, catalog names, and target hosts are known.

### LH-002 — PostgreSQL wire acknowledges transactions that are never executed

**Priority:** P1 High
**Status:** Confirmed correctness defect; documented as a limitation but unsafe for writable clients.

#### Evidence

[`src/Lakehold.Api/PgWire/PgCatalogShim.cs`](../src/Lakehold.Api/PgWire/PgCatalogShim.cs) returns successful command completion for `BEGIN`, `COMMIT`, and `ROLLBACK` without creating transaction state. Each statement is resolved and executed independently through a fresh scoped service. [`POSTGRES-WIRE.md`](POSTGRES-WIRE.md#deliberately-not-implemented) confirms that real transactions are not implemented.

Configured PostgreSQL-wire passwords grant writable catalog access. API tokens can also carry write capability. A client can therefore receive successful transaction responses around statements that were never atomic.

#### Impact

A generic client can execute several writes, encounter a later error, issue `ROLLBACK`, receive success, and still retain the earlier committed writes. This can produce partial updates or schema changes while the client reports that the transaction was rolled back.

#### Required remediation

- Until real connection-scoped transactions exist, return PostgreSQL `0A000 feature_not_supported` for transaction-control statements.
- Prefer an explicitly read-only PostgreSQL-wire profile for BI tools.
- If writable wire support remains a goal, hold a real session and transaction across the connection and test commit, rollback, disconnect, timeout, and failover semantics with a real PostgreSQL driver.

#### Exit gate

No client can receive successful transaction-control responses unless the enclosed statements have actual PostgreSQL-compatible atomicity and rollback behaviour.

### LH-003 — Shared PostgreSQL-wire passwords allow tenant impersonation

**Priority:** P1 High
**Status:** Confirmed unsafe mode; accurately warned about in source and documentation.

#### Evidence

[`src/Lakehold.Api/PgWire/PgWireOptions.cs`](../src/Lakehold.Api/PgWire/PgWireOptions.cs) states that the legacy shared password authenticates a connection but not the tenant it names. Any holder can present a different tenant as the PostgreSQL username. [`compose.production.yaml`](../compose.production.yaml) exposes the shared-password environment variable as the simplest production configuration path.

Per-tenant passwords and LakeHold API-token authentication correctly bind credentials to tenant identity, but the shared mode remains available.

#### Impact

On a multi-tenant node, one leaked or deliberately shared credential becomes a credential for every tenant exposed through the wire endpoint.

#### Required remediation

- Refuse shared-password mode in Production when more than one tenant exists.
- Prefer LakeHold API tokens, because they are revocable and already carry tenant, catalog, and capability scope.
- Retain per-tenant passwords only as a compatibility mode with explicit operator acknowledgement.

### LH-004 — Per-session limits do not provide aggregate node admission control

**Priority:** P1 High
**Status:** Confirmed resource-isolation gap.

#### Evidence

[`src/Lakehold.Engine/Configuration/LakehouseOptions.cs`](../src/Lakehold.Engine/Configuration/LakehouseOptions.cs) defaults each compute session to a 2 GB memory ceiling and four threads, with up to 32 warm sessions. [`compose.production.yaml`](../compose.production.yaml) defaults the whole API container to 4 GB.

The session gate serialises statements within one `Duckling`, but different tenants and catalogs can execute concurrently. `MaxWarmSessions` is an eviction limit, not a global execution or memory budget.

#### Impact

Concurrent tenants can collectively exceed container memory or CPU capacity, causing query failures, severe latency, or container termination. The theoretical sum of per-session ceilings greatly exceeds the node limit, even though memory is not reserved eagerly.

#### Required remediation

- Add a node-wide execution semaphore and resource budget.
- Add per-tenant and per-principal concurrency and cost quotas.
- Create separate workload classes for interactive queries, BI streams, ingestion, maintenance, and operator tasks.
- Bound queues and return explicit capacity responses rather than waiting indefinitely.
- Export queue depth, execution slots, rejection counts, memory pressure, spill, and timeout telemetry.

### LH-005 — PostgreSQL wire can fail to bind while readiness remains healthy

**Priority:** P1 High
**Status:** Confirmed availability defect.

#### Evidence

[`src/Lakehold.Api/PgWire/PgWireServer.cs`](../src/Lakehold.Api/PgWire/PgWireServer.cs) catches `SocketException` when the configured port cannot bind, logs the failure, and returns from the background service while leaving the HTTP host alive. Readiness in [`src/Lakehold.Api/Health/ControlPlaneHealthCheck.cs`](../src/Lakehold.Api/Health/ControlPlaneHealthCheck.cs) checks only the PostgreSQL control plane.

#### Impact

An operator can enable the BI endpoint, deploy successfully, and receive a healthy `/health` response while the requested PostgreSQL-wire service is absent.

#### Required remediation

- Track listener state explicitly.
- When PostgreSQL wire is enabled, add listener state to readiness but not liveness.
- Expose the listener's enabled/bound/faulted state through operational diagnostics and telemetry.

### LH-006 — Incremental connectors do not represent source deletions

**Priority:** P1 High
**Status:** Confirmed and documented data-fidelity gap.

#### Evidence

[`CONNECTORS.md`](CONNECTORS.md#current-limitations) states:

- PostgreSQL is ordered polling rather than logical replication and has no delete capture.
- HubSpot source-side deletions are not represented.
- Kafka Avro tombstones advance offsets without staging a delete row.

#### Impact

LakeHold target tables can retain records that no longer exist upstream. This prevents a general claim that incremental connectors maintain an exact current-state representation.

#### Required remediation

- Define a canonical change envelope containing operation type, key, ordering/checkpoint, schema identity, and complete after-image where applicable.
- Map Kafka tombstones and database/SaaS delete events to governed target deletes.
- Add idempotent replay and recovery tests for delete events.
- Until supported, label affected adapters as append/update-only or lacking delete fidelity.

### LH-007 — Control-plane recovery is not automated or proven end to end

**Priority:** P1 High
**Status:** Confirmed production-readiness gap.

#### Evidence

LakeHold has catalog metadata backup/restore, signed eject, PostgreSQL-backed control-plane migrations, advisory locking, and detailed runbooks. However, [`PRODUCTION-READINESS-ROADMAP.md`](PRODUCTION-READINESS-ROADMAP.md#phase-5--make-recovery-and-ha-operationally-credible) and [`OPERATIONS.md`](OPERATIONS.md#state-and-backup-boundaries) correctly state that PostgreSQL control-plane and DuckLake metadata recovery depend on external native backup/PITR.

There is no repository-owned end-to-end restore workflow that recreates tenants, memberships, tokens, catalog descriptors, connector definitions and checkpoints, subscriptions, operations, and audit evidence on a fresh deployment.

#### Impact

Recoverable table data does not make the service operable if tenancy, identity, connector state, or metadata relationships are lost. Backups kept on the same host or volume also do not protect against loss of that failure domain.

#### Required remediation

- Automate control-plane backup and fresh-deployment restore.
- Require off-host storage and PostgreSQL PITR.
- Publish supported RPO and RTO targets.
- Add upgrade and restore fixtures from every supported released schema.
- Independently verify tenant-scoped row counts, manifests, connector checkpoints, token revocation, and subscriptions after restore.

### LH-008 — Outbound destination policy does not fully cover NAT64 and special-use networks

**Priority:** P2 Medium
**Status:** Defence-in-depth gap; exploitability depends on deployment network topology.

#### Evidence

[`src/Lakehold.Api/Security/OutboundDestinationPolicy.cs`](../src/Lakehold.Api/Security/OutboundDestinationPolicy.cs) rejects IPv4-mapped IPv6, loopback, RFC1918, link-local, multicast, CGNAT, and unique-local IPv6. It does not decode NAT64 translation prefixes or comprehensively classify all IANA special-purpose address ranges.

#### Impact

On NAT64/DNS64-enabled networks, a syntactically global IPv6 address may translate to a non-public IPv4 destination while passing the current classifier. Incomplete special-use classification also makes the documented "public destination" guarantee broader than the implementation.

#### Required remediation

- Use a complete, maintained special-purpose CIDR policy.
- Normalise IPv4-mapped and translated address forms before classification.
- Test NAT64, DNS changes, redirects, mixed A/AAAA results, and connection-time rebinding.
- Pair application validation with an outbound proxy or firewall and use operator host allowlists in Production.

### LH-009 — API and wire authentication/resource throttling is incomplete

**Priority:** P2 Medium
**Status:** Confirmed abuse-control gap.

#### Evidence

The isolated LINQ compiler uses an ASP.NET rate limiter, but the primary API does not register a general rate limiter. [`AUTHENTICATION.md`](AUTHENTICATION.md#still-open) records per-principal quotas and HTTP authentication-attempt rate limiting as open. The PostgreSQL-wire endpoint counts failures but does not implement progressive delay or lockout.

#### Impact

API abuse, repeated authentication attempts, expensive query submission, and endpoint saturation depend on an external ingress being configured correctly. Air-gapped or simple Compose deployments may not have that control.

#### Required remediation

- Add subject/token-prefix/IP-partitioned API limits.
- Apply stricter unauthenticated and authentication-failure policies.
- Add progressive wire-authentication delay or bounded lockout without creating an easy account-denial attack.
- Document which limits belong in LakeHold and which must be enforced by the ingress.

### LH-010 — Core engine and wire options lack complete startup validation

**Priority:** P2 Medium
**Status:** Confirmed configuration gap.

#### Evidence

[`src/Lakehold.Api/Program.cs`](../src/Lakehold.Api/Program.cs) binds `LakehouseOptions` through plain `Configure`, while connector, CDC, and query-planner options use explicit validation and `ValidateOnStart`. PostgreSQL-wire configuration performs several security checks manually but does not validate every numeric and timeout bound.

#### Impact

Invalid memory strings, zero or negative session/thread/row values, impossible timeouts, invalid connection counts, and oversized or nonsensical protocol limits can fail late, disable a surface, or produce surprising behaviour.

#### Required remediation

- Add `IValidateOptions<LakehouseOptions>` and `ValidateOnStart`.
- Add the equivalent complete validator for `PgWireOptions`.
- Validate resolved paths, memory-limit syntax, positive counts, timeout relationships, and safe upper bounds.
- Cover invalid deployment configuration with startup tests.

### LH-011 — Release supply-chain controls are incomplete

**Priority:** P2 Medium
**Status:** Confirmed delivery gap.

#### Evidence

- [`ci.yml`](../.github/workflows/ci.yml) provides broad functional coverage but does not make dependency vulnerability review, secret scanning, SAST, container scanning, or SBOM generation explicit merge gates.
- [`release.yml`](../.github/workflows/release.yml) builds and pushes images without image attestation, signing, or vulnerability scanning.
- GitHub Actions use mutable major-version tags.
- Dockerfiles use mutable base-image tags such as `.NET 10.0`, `node:24-alpine`, and `nginx:alpine`.
- Production Compose defaults application images to `latest`, even though the operations guide correctly tells operators to pin a release.

#### Dependency scan results from this review

- `dotnet list Lakehold.slnx package --vulnerable --include-transitive`: no known vulnerable packages from the configured NuGet sources.
- `npm audit --omit=dev`: zero runtime findings.
- Full `npm audit`: five development-toolchain findings, comprising four high and one moderate issue through `brace-expansion`, `fast-uri`, `hono`, `ip-address`, and `nanoid`.

These JavaScript findings were development-only in the inspected dependency graph; they should not be described as shipped browser-runtime vulnerabilities.

#### Required remediation

- Pin actions to commit SHAs and production base images to digests.
- Add dependency, secret, SAST, and image scanning with reviewed exception handling.
- Generate an SBOM and provenance for every release artifact.
- Sign release images and verify signatures in deployment guidance.
- Prefer image digests for high-assurance deployment records rather than relying on mutable tags.

### LH-012 — Workbench edge security and OIDC callback logging need hardening

**Priority:** P2 Medium
**Status:** Confirmed web-security gap.

#### Evidence

- [`web/lakehold-ui/nginx.base.conf`](../web/lakehold-ui/nginx.base.conf) does not set a Content Security Policy, `frame-ancestors`, `X-Content-Type-Options`, referrer policy, permissions policy, or explicit server-token policy.
- Its access-log format records the complete request, including query parameters.
- [`web/lakehold-ui/nginx.workbench.conf`](../web/lakehold-ui/nginx.workbench.conf) proxies `/auth/callback` through that server, so short-lived OIDC `code` and `state` parameters enter access logs.
- [`web/lakehold-ui/src/app/auth.service.ts`](../web/lakehold-ui/src/app/auth.service.ts) intentionally keeps break-glass bearer tokens in session storage by default and optionally in local storage.
- Repository-authored Markdown is compiled and trusted with `bypassSecurityTrustHtml`. It is not user input today, but it increases the impact of a compromised documentation/build input.

#### Impact

The current design increases the impact of any Workbench XSS or compromised frontend dependency. Authentication callback parameters also enter retained infrastructure logs unnecessarily.

#### Required remediation

- Add a restrictive CSP tailored separately for the Workbench and public website.
- Set `frame-ancestors`, `nosniff`, referrer, and permissions policies; set HSTS at the TLS terminator.
- Log callback paths without query strings or suppress access logging for the callback location.
- Keep OIDC HttpOnly sessions as the preferred human authentication path.
- Keep persistent browser bearer-token storage explicitly labelled as a break-glass compatibility choice.

### LH-013 — Bootstrap-token delivery through logs expands credential exposure

**Priority:** P2 Medium
**Status:** Intentional design with production risk.

#### Evidence

[`src/Lakehold.Api/TokenBootstrap.cs`](../src/Lakehold.Api/TokenBootstrap.cs) mints the first instance-scoped credential and logs the plaintext once when no external bootstrap token is supplied. [`compose.production.yaml`](../compose.production.yaml) permits the external value to remain empty and instructs the operator to recover the minted token from logs.

#### Impact

Central logging, support bundles, container log drivers, or broad log-reader access can retain a long-lived provisioning credential. "Logged once" does not mean "retained once" in modern logging pipelines.

#### Required remediation

- In Production, require explicit opt-in before minting a credential into logs.
- Prefer an externally injected one-time bootstrap credential or a root-owned one-time secret file.
- Add an explicit break-glass rotate/recover/revoke workflow.
- Alert on bootstrap credential use after initial provisioning.

### LH-014 — API container hardening is weaker than the LINQ compiler sandbox

**Priority:** P2 Medium
**Status:** Confirmed deployment gap.

#### Evidence

[`compose.production.yaml`](../compose.production.yaml) gives the optional LINQ compiler a read-only root filesystem, dropped capabilities, `no-new-privileges`, PID/CPU/memory limits, and bounded temporary storage. The API container has only a memory limit and writable state/import volumes.

#### Impact

The API is the component that executes DuckDB SQL and holds storage/metadata access. Its weaker container boundary increases the impact of LH-001 and of any native-library or application compromise.

#### Required remediation

- Drop all unnecessary Linux capabilities and set `no-new-privileges`.
- Use a read-only root filesystem with explicit writable state, import, and temporary mounts.
- Add CPU and PID limits and bounded/noexec temporary filesystems where compatible.
- Run the SQL worker separately from the control-plane/API process so its mounts and credentials can be narrower.

### LH-015 — Security-critical adversarial tests do not prove the real isolation boundary

**Priority:** P2 Medium
**Status:** Confirmed verification gap.

#### Evidence

The suite strongly covers authentication, tenant-qualified catalog resolution, PostgreSQL/S3 integration, connector transports, PostgreSQL-wire protocol, browser journeys, and SDK conformance. It does not contain a production-path test showing that direct SQL cannot use `glob`, external readers, new attachments, known cross-tenant paths, remote URLs, or runtime files.

The only inspected test that rejects `read_parquet('s3://outside/...')` exercises the external query-planner contract, not the direct SQL endpoint.

#### Required remediation

Add dedicated tests for:

- Two tenants with deliberately identical catalog names.
- Known and discoverable cross-tenant local paths.
- Shared-bucket paths with tenant-scoped credentials.
- `ATTACH`, `COPY`, `glob`, file readers, secret functions, extension install/load, and remote URLs.
- Network exfiltration attempts and DNS/NAT64 variants.
- Aggregate memory/concurrency exhaustion.
- Enabled PostgreSQL-wire listener failure and readiness.
- Transaction rollback correctness or explicit rejection.

### LH-016 — Readiness and operations documentation has drifted from the implementation

**Priority:** P2 Medium
**Status:** Corrected by the 10 August 2026 documentation follow-up. Automated drift prevention remains open.

#### Evidence

- Before the follow-up, [`OPERATIONS.md`](OPERATIONS.md#rollback) said the control plane still used
  additive schema initialisation, while [`src/Lakehold.ControlPlane/Data/ControlPlaneDatabase.cs`](../src/Lakehold.ControlPlane/Data/ControlPlaneDatabase.cs)
  ran EF migrations under a PostgreSQL advisory lock.
- The public comparison content said same-name catalog isolation remained unfinished, while tenant
  identity already participated in storage namespaces and the warm-session key.
- Comments in `Program.cs` and `auth.service.ts` discussed optional authentication, while
  [`src/Lakehold.Api/Auth/LakeholdAuthOptions.cs`](../src/Lakehold.Api/Auth/LakeholdAuthOptions.cs)
  explicitly removes that switch and requires authentication except for a deliberately scoped demo identity.
- [`PRODUCTION-READINESS-ROADMAP.md`](PRODUCTION-READINESS-ROADMAP.md) understated current migration
  and CI coverage. Its arbitrary-SQL containment warning remains correct.

#### Impact

Stale warnings can understate completed controls, hide the actual remaining blocker, and cause operators to choose incorrect upgrade or rollback procedures.

#### Follow-up and remaining remediation

- **Completed 10 August 2026:** reconcile the readiness roadmap, operations guide, comparison page,
  source comments, authentication UI copy, SDK claims, and MCP read-only wording.
- **Completed 10 August 2026:** add review dates, implementation status, and evidence links where the
  repository provides them.
- Generate or test important status/configuration claims where practical.
- Keep proposal, roadmap, implemented source, released artifact, and verified production behaviour distinct.

### LH-017 — Kubernetes deployment claims are not backed by supported deployment artifacts

**Priority:** P2 Medium
**Status:** Documentation claim corrected; product/operations support gap remains.

#### Evidence

Before the follow-up, architecture and product documentation described deployment to Kubernetes,
but the repository contained no Helm chart, Kubernetes manifests, operator, or Kubernetes-specific
production verification gate. The documentation now describes the containers as portable and the
supported repository deployment profile as Compose on a laptop or VM.

#### Impact

"Container deployable on Kubernetes" is not equivalent to a supported Kubernetes deployment. Operators must invent readiness, persistent-volume, secret, upgrade, disruption-budget, networking, and recovery behaviour themselves.

#### Required remediation

- **Completed 10 August 2026:** qualify the current claim as container portability unless Kubernetes
  becomes a supported target.
- If supported, provide Helm/manifests, documented storage classes and secrets, probes, disruption budgets, upgrade/rollback steps, and an automated cluster smoke test.

### LH-018 — Governance, semantic, BI, interoperability, and SDK delivery remain partial

**Priority:** P2 Medium
**Status:** Product gap group; mostly represented accurately in the enterprise roadmap.

| Area | Current capability | Remaining issue |
|---|---|---|
| Governance | Owners, descriptions, tags, quality policy, audit, connector-run lineage, three roles | No row/column policies, searchable catalog/glossary, classification, sensitivity labels, freshness objectives, or complete lineage graph |
| Semantic layer | SQL, EF Core, MCP, saved queries | No governed metric definitions or reusable semantic models |
| BI | PostgreSQL wire works with psql, DBeaver, and Npgsql | Power BI still needs type-catalog compatibility; no supported JDBC/ODBC strategy |
| Open interoperability | Verified reader-independent eject/export | No live Iceberg REST or equivalent multi-engine catalog access |
| Time travel API | Snapshot listing/detail/preview and partial restore | No query `asOf`, labels, pins, retention policy, or catalog-wide restore |
| Connectors | Five built-in adapters with durable scheduling, retries, checkpoints, and lineage | No broad certified catalogue, delete fidelity, nested mappings, or cron/event-driven scheduling |
| HA | PostgreSQL control plane, leases, and shared configuration identity | Queries remain worker-local; multi-node failover and token/CDC/artifact behaviour are not fully proven |
| Public SDKs | Generated Java, Go, .NET, and Python sources with conformance work | Public package publication, indexing, and clean-consumer proof remain incomplete |

Primary source: [`ENTERPRISE-DATA-PLATFORM-ROADMAP.md`](ENTERPRISE-DATA-PLATFORM-ROADMAP.md).

### LH-019 — Tenant identity is not a mandatory engine descriptor invariant

**Priority:** P3 Low
**Status:** Latent isolation footgun rather than a confirmed production-path exploit.

#### Evidence

[`src/Lakehold.Engine/Catalog/CatalogDescriptor.cs`](../src/Lakehold.Engine/Catalog/CatalogDescriptor.cs) defaults `TenantKey` to an empty string and `CatalogId` to zero. [`src/Lakehold.Engine/Catalog/CatalogStorageNamespace.cs`](../src/Lakehold.Engine/Catalog/CatalogStorageNamespace.cs) retains a legacy catalog-only path when the tenant key is blank.

The current control-plane `Catalog.ToDescriptor()` path supplies both values correctly, but the engine type itself permits an unsafe legacy state.

#### Required remediation

- Make tenant identity and catalog identity required for production descriptors.
- Isolate legacy import/migration behaviour from the normal runtime type.
- Fail closed when a production query attempts to construct or attach an unqualified descriptor.

### LH-020 — Query errors are returned verbatim to authenticated callers

**Priority:** P3 Low
**Status:** Deliberate developer-experience trade-off.

#### Evidence

The HTTP and PostgreSQL-wire paths forward DuckDB error text because it identifies the failing token and helps the Workbench behave like an IDE.

#### Impact

Engine errors can disclose physical paths, object-store locations, catalog internals, extension details, or provider implementation information to an authenticated tenant.

#### Required remediation

- Preserve actionable syntax and binder detail while redacting configured state roots, scratch paths, metadata connection details, and credential-bearing URIs.
- Retain complete diagnostics in protected server logs and traces.
- Add regression tests for known path/secret-bearing failure shapes.

### LH-021 — Large implementation hotspots increase coupling and review risk

**Priority:** P3 Low
**Status:** Confirmed maintainability gap.

Approximate inspected sizes:

| File | Lines |
|---|---:|
| `src/Lakehold.Api/Endpoints/LakehouseEndpoints.cs` | 1,827 |
| `src/Lakehold.ControlPlane/Model/Entities.cs` | 1,676 |
| `src/Lakehold.ControlPlane/Data/LakehouseService.cs` | 1,196 |
| `src/Lakehold.Api/PgWire/PgWireConnection.cs` | 1,139 |
| `web/lakehold-ui/src/app/workbench.component.ts` | 1,078 |

The project-level boundaries are sensible: API, ControlPlane, Engine, Querying, and the isolated LINQ compiler have distinct responsibilities. The problem is concentration within those projects. Protocol, authorization, orchestration, persistence, error mapping, and UI state increasingly meet in a few broad files.

#### Required remediation

- Split incrementally by vertical application behaviour rather than creating generic technical layers.
- Extract PostgreSQL-wire protocol state and compatibility shims behind focused tests.
- Move endpoint orchestration into small application handlers while keeping capability checks transport-neutral.
- Divide Workbench state into feature facades/components.
- Keep the engine application-agnostic and avoid moving tenant policy or business semantics into it.
- Do not perform a speculative rewrite; address hotspots as correctness and containment work touches them.

## Controls that are already strong

These strengths should be preserved while remediating the findings:

- Authentication is unconditional except for an explicitly configured, catalog-scoped demo reader.
- API tokens are returned once, stored hashed, tenant/catalog scoped, capability-bearing, and auditable.
- OIDC and API-token callers resolve to one transport-neutral principal and capability policy.
- Tenant identity participates in catalog lookup, warm-session identity, storage paths, and audit.
- PostgreSQL-backed control-plane migrations run under an advisory lock.
- Storage credentials remain deployment-owned; catalog records retain references and generated secret names rather than raw credentials.
- PostgreSQL metadata credentials are dropped after catalog attachment except for bounded privileged maintenance operations.
- Managed connectors have durable definitions, schedules, leases, fencing, checkpoints, retries, schema/quality policy, scratch limits, and run lineage.
- CDC, signed webhooks, scheduled maintenance, catalog backup/restore, signed eject, and OpenTelemetry are substantial operational foundations.
- The LINQ compiler has a much stronger isolation and resource posture than an in-process general-purpose compiler would have.
- CI covers backend, frontend, public SDKs, PostgreSQL/S3 integration, Kafka Avro through trusted gateways, Chromium user journeys, and a disposable production-operator path.

## Verification performed

### Successful checks

- `dotnet list Lakehold.slnx package --vulnerable --include-transitive`
- `npm audit --omit=dev`
- Full `npm audit` for development dependency visibility
- Prettier check across frontend TypeScript, HTML, and CSS
- Anonymous HTTP smoke checks against the running local API; protected access and tenant routes returned `401`
- Repository structure, configuration, workflow, container, runbook, source, and test inspection
- Hosted CI status inspection for committed review head `34b160e`; all eight PR checks completed successfully before PR #108 merged

Approximate static test counts at review time:

- 407 API `[Fact]`/`[Theory]` tests
- 89 engine `[Fact]`/`[Theory]` tests
- 306 frontend `it`/`test` cases

### Limitations

- A full local `make test` run was started but deliberately cancelled because another process changed the shared checkout during execution. It is not recorded as either success or product failure.
- Cleanup encountered sandbox denial when attempting to access Docker's socket. No additional product test containers remained afterward.
- A harmless live SQL-containment probe was not attempted without an appropriate tenant credential. LH-001 is grounded in the direct execution path, shared mounts, available DuckDB capabilities, absence of containment configuration, and the repository's own release-blocker assessment.
- The checkout changed branches and commits during the review. The platform-core files were compared across the committed heads used for analysis and were unchanged; concurrent SDK and website work was excluded.
- Hosted CI for one committed head does not validate later uncommitted or newly committed work.

## Recommended delivery sequence

### Stage 1 — Close the shared-tenancy release blocker

1. Write the explicit arbitrary-SQL threat model.
2. Introduce tenant query workers with tenant-only mounts, credentials, egress, and resource limits.
3. Apply DuckDB external-access restrictions and configuration locking after trusted setup.
4. Add the adversarial two-tenant production-path suite.

### Stage 2 — Correct PostgreSQL-wire semantics and health

1. Make the endpoint read-only by default.
2. Reject unsupported transactions instead of acknowledging them.
3. Remove or production-gate shared-password mode.
4. Add listener readiness and complete options validation.

### Stage 3 — Bound shared resources and abuse

1. Add aggregate workload admission and workload classes.
2. Add per-tenant/per-principal quotas and bounded queues.
3. Add API and wire authentication throttling.
4. Export capacity and rejection telemetry.

### Stage 4 — Make state fidelity and recovery credible

1. Implement canonical deletion semantics for incremental connectors.
2. Automate control-plane and metadata restore on a fresh deployment.
3. Verify RPO/RTO, tenant row counts, manifests, checkpoints, subscriptions, and revocation.

### Stage 5 — Harden delivery and deployment

1. Close NAT64/special-network egress gaps.
2. Harden Workbench headers, callback logging, API container, and bootstrap-token delivery.
3. Add SBOM, provenance, signing, vulnerability scanning, and immutable dependency/image pins.

### Stage 6 — Reconcile claims, then expand the platform

1. **Completed 10 August 2026:** correct readiness, operations, comparison, and authentication documentation.
2. Kubernetes is documented as container portability rather than a supported target; decide whether
   to build and validate the missing support artifacts.
3. Continue governance, semantic, Power BI, interoperability, connector, and SDK delivery work.
4. Split implementation hotspots only as this work touches them.
5. Add automated checks or review ownership for security-boundary and status claims that can drift.

## Production decision gates

LakeHold should not be described as production-ready for shared, mutually untrusted multi-tenancy until all of the following are true:

- LH-001 has passed adversarial cross-tenant filesystem, network, secret, attachment, and resource tests.
- Node-wide workload admission prevents collective session limits from exhausting the node.
- Enabled optional services participate correctly in readiness.
- A clean deployment can be restored from off-host backups and independently verified.
- Security-critical checks run as required merge/release gates.
- PostgreSQL-wire transaction and credential semantics cannot mislead or cross tenant boundaries.

For a trusted single-tenant deployment, the remaining acceptance decision should explicitly cover recovery, ingress rate limits, pinned images, PostgreSQL backup/PITR, Workbench/OIDC hardening, connector delete fidelity, and the absence of a supported Kubernetes profile where relevant.
