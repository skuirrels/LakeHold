# CDC robustness and DuckDB replication execution plan

**Status:** Initial end-to-end implementation completed on 2026-07-30; production exit gates remain
open as listed below.

**Purpose:** Make LakeHold's change feed lossless, secure, observable, and safe under backlog,
retries, retention, and multi-node dispatch, then use that hardened feed to maintain a
transactionally consistent mirror in another DuckDB database.

This plan does not supersede `PRODUCTION-READINESS-ROADMAP.md`. Its tenant-qualification proof,
authentication hardening, and arbitrary-SQL containment gates still govern an untrusted shared
deployment.

## Execution record

Implemented:

- opaque, range-bound keyset cursors with deterministic update pre/post ordering and a
  snapshot-scoped pull route;
- one-snapshot webhook delivery, durable PostgreSQL delivery rows, stable retry identity/body with
  fresh attempt signatures, optimistic expiring leases, and bounded parallel subscription processing;
- versioned HMAC signing over timestamp, delivery id, and exact body bytes;
- HTTPS-by-default destination policy, optional hostname allowlist, prohibited-address checks on
  every attempt, and disabled redirects;
- pull-consumer checkpoints, retention refusal, subscription pause/resume, secret replacement,
  replay, retry-now, and catalog-delete cleanup;
- CDC delivery, payload, lag, worker, and lease instruments;
- `Lakehold.Client`, the transport-neutral replication/apply layer, a DuckDB target, a runnable
  worker, and an example configuration;
- exact-snapshot bootstrap with row-count verification, cursor-drained catch-up, keyed and
  append-only policies, atomic target rows/checkpoint, source acknowledgement after commit, and
  fail-closed snapshot-gap/schema-fingerprint handling;
- focused tests for a 10,050-row snapshot, page-boundary updates, stable retries, two concurrent
  dispatchers, retention refusal, prohibited destinations, target rollback/idempotency, and
  bootstrap-plus-catch-up.

Still required before the production-ready outcome in this document may be claimed:

- a historical snapshot manifest that preserves affected dropped/renamed tables and immutable
  source/catalog identity;
- the 100,000-row/type-boundary/schema-lifecycle matrix and a PostgreSQL multi-node kill/takeover
  lane in `make test`;
- overlapping signing-secret rotation, `Retry-After`/jitter, explicit request/response byte limits,
  pending-delivery inspection, destination test, and confirmed abandon workflows;
- complete observable pending/oldest-watermark/wall-time-lag gauges and executable alert validation;
- bulk Arrow/Parquet bootstrap, immutable target ownership locking, supported schema evolution,
  complex type mappings, content digests, and deliberate target-drift verification;
- disposable source-to-target restart/outage/retention races and canary rollout evidence.

Until those gates close, the worker is a functional first slice for selected scalar keyed or
append-only tables, not a blanket production-ready replication claim.

## Outcome

When this plan is complete:

- every committed row change can be read exactly once by position, including more than 10,000
  changes in one table and one snapshot;
- webhook delivery remains at-least-once, while every retry of one logical delivery has the same
  identity and body plus a fresh, verifiable attempt timestamp and signature;
- one subscription is processed in snapshot order by only one active worker at a time, even when
  several LakeHold API nodes are running;
- snapshot expiry cannot remove changes still required by a subscription or replica;
- webhook destinations are subject to explicit egress policy and DNS/redirect revalidation;
- operators can pause, resume, replay, rotate, inspect, and alert on CDC delivery without editing
  PostgreSQL directly;
- a first-party replicator can bootstrap a target DuckDB at source snapshot `S` and apply
  `S + 1` onward transactionally;
- a retry or crash cannot leave target rows ahead of or behind the target checkpoint;
- schema changes and unsupported table shapes fail closed rather than silently diverging.

## Scope and non-goals

### In scope

- DuckLake-backed LakeHold catalogs as CDC sources.
- The existing HTTP pull feed and signed webhook subscription surface.
- PostgreSQL-backed CDC delivery state.
- A local or mounted DuckDB database file as the first replication target.
- One-way, source-authoritative replication.
- Initial snapshot bootstrap, schema establishment, row changes, checkpoints, verification, and
  recovery.
- A reusable .NET client and replication engine, plus an executable worker.

### Not in the first release

- Bidirectional or multi-master replication.
- Conflict resolution between independently writable source and target tables.
- Replicating arbitrary DuckDB databases that are not managed by DuckLake. Plain DuckDB does not
  provide LakeHold's snapshot change feed.
- Exactly-once network delivery. The delivery contract remains at-least-once; the target obtains
  exactly-once *effects* by committing changes and its checkpoint in one DuckDB transaction.
- Direct copying of DuckLake metadata or data files into the target.
- A generic Debezium/Kafka protocol implementation.
- A LakeHold-to-LakeHold target in the first slice. That can follow once origin tagging and loop
  prevention are designed.

## Current baseline

The current implementation already has useful foundations:

- `ChangeFeed` reads `ducklake_table_changes` and reports insert, delete, update pre-image, and
  update post-image rows.
- Snapshot ranges are inclusive and callers resume from the last completed snapshot plus one.
- Pull results and webhook table payloads are bounded.
- Webhooks are HMAC-SHA256 signed with a per-subscription secret.
- The dispatcher persists a snapshot cursor, advances it only after a 2xx, records failures, uses a
  timeout, and applies exponential backoff.
- Existing integration tests exercise a real DuckLake catalog with only the outbound HTTP hop
  stubbed.

The following gaps are the reason hardening must precede replication:

1. A truncated pull page has no continuation cursor. The public API caps a page at 10,000 rows, so
   one snapshot with more than 10,000 changes cannot be drained.
2. The dispatcher currently reads `LastDeliveredSnapshot + 1` through the latest snapshot in one
   window, although the documented contract says one snapshot at a time.
3. Retry attempts generate a new delivery id and a new `DeliveredUtc`, so their body, signature,
   and identity are not stable.
4. all-table subscriptions enumerate current tables. A table changed and then dropped before a
   poll can disappear from discovery before its changes are delivered.
5. snapshot expiry has no watermark tied to the oldest undelivered subscription or replica
   checkpoint.
6. every API node dispatches independently. This permits duplicate and out-of-order overlapping
   deliveries and cursor regression.
7. subscriptions are processed serially in one sweep, so one slow endpoint delays later
   subscriptions.
8. arbitrary HTTP and HTTPS destinations are accepted without application-level SSRF protection.
9. the advertised timestamp header and replay-verification contract do not match the signer.
10. there is no safe update, secret rotation, pause/resume, or replay operation.
11. delivery observability is state and logs only; there is no complete metrics/alerting surface.
12. row changes alone do not carry enough schema and replication-identity information to maintain a
    general target safely.

## Architecture decisions

These decisions should be treated as implementation constraints.

### The pull feed is authoritative

A webhook is a signed wake-up notification. A durable consumer, including the DuckDB replicator,
must use the pull API to enumerate and read the authoritative snapshot. It must not depend on the
webhook's inline row prefix for correctness.

Inline changes may remain as a convenience for small integrations, but a truncated webhook must
never be the only route to the omitted rows.

### One delivery represents one source snapshot

The durable ordering unit is a DuckLake snapshot. The dispatcher creates and completes deliveries
in ascending snapshot order. A source transaction that changes several tables is applied to a
replica as one target transaction.

### CDC position is finer than a snapshot

The pull protocol needs an opaque continuation cursor within one table and snapshot. Before fixing
the cursor format, verify the uniqueness and stability of DuckLake's `(snapshot_id, rowid,
change_type)` tuple on the pinned DuckDB/DuckLake version.

Preferred ordering:

1. `snapshot_id`;
2. `rowid`;
3. an explicit change-type ordinal where update pre-image precedes update post-image.

If this tuple is not guaranteed unique and stable, LakeHold must materialise a stable event ordinal
in its own CDC event store. Do not expose a continuation format that can skip or repeat tied rows.
The public cursor must be opaque and versioned so its internal representation can change.

### Delivery identity is logical, not per HTTP attempt

One subscription and source snapshot receive one durable delivery id, body, and creation timestamp.
All HTTP retries reuse the id and body. Each attempt receives a fresh signed timestamp so receivers
can enforce freshness even after a long outage; attempt count and attempt time are separate
delivery-state fields.

### PostgreSQL coordinates dispatch

Use a PostgreSQL delivery/outbox row and a bounded lease to claim work. A node may process many
different subscriptions concurrently, but only the oldest outstanding delivery for a given
subscription may be claimed.

Use row locking or an atomic claim with `FOR UPDATE SKIP LOCKED`, a lease expiry, and optimistic
state transitions. A process crash returns the delivery to the queue after the lease expires.

### Retention follows the slowest durable consumer

Calculate a catalog retention watermark from every live webhook subscription, including paused
subscriptions, and every active registered replica. An applied expiry operation must refuse to
remove required CDC history unless the operator explicitly abandons or re-bootstraps those
consumers.

The refusal must name the blocking consumer and oldest required snapshot without revealing another
tenant's state.

### Replication requires an identity policy

DuckLake's change `rowid` pairs an update's pre-image and post-image, but it must not be assumed to
be a portable application primary key.

The first replicator supports:

- **keyed tables:** one or more configured columns form a unique replication key;
- **append-only tables:** inserts are allowed; update or delete blocks the replica.

A mutable table with no reliable unique key is unsupported in the first release. It must fail
validation before bootstrap rather than risk deleting or updating duplicate rows.

### The target is source-authoritative

Replicated target tables are dedicated mirrors. Application writes to them are unsupported.
Checkpoint verification and optional content checks detect drift; recovery is re-bootstrap, not
automatic conflict resolution.

## Phase 0 — Pin the failing contracts

**Goal:** Turn every known gap into a failing automated test before changing behaviour.

### Work

- Add an engine test with more than 10,000 changes in one table and one snapshot. Prove that every
  event can be paged without repetition or omission.
- Add update-boundary tests where a page ends between pre-image and post-image.
- Verify whether `(snapshot_id, rowid, change_type)` is unique for inserts, deletes, updates,
  duplicate logical rows, key changes, and multi-statement transactions.
- Add API tests proving a dispatcher creates one delivery per snapshot rather than one range to
  the latest snapshot.
- Promote the Phase 2 retry assertions into a focused dispatcher test: delivery id and body remain
  byte-for-byte stable after 503, timeout, and worker restart, while every attempt has a fresh
  verifiable timestamp and signature.
- Add an all-table lifecycle test: create, write, and drop a table before dispatch.
- Add snapshot-expiry tests with a lagging subscription.
- Add two-node dispatcher tests against the same PostgreSQL control plane.
- Add slow-endpoint tests proving bounded parallelism and per-subscription ordering.
- Add destination-policy tests for loopback, link-local, private ranges, cloud metadata addresses,
  DNS rebinding, redirects, excessive redirects, and oversized response headers.
- Add schema-evolution feed tests for table create/drop/rename and column add/drop/rename/type
  change.
- Add wire-type tests for nulls, booleans, signed and unsigned integers, huge integers, decimals,
  floating point, UUID, blob, date, timestamp, timestamp with time zone, interval, list, struct, and
  map values.

### Exit criteria

- Tests reproduce the current continuation, retry identity, table lifecycle, retention, and
  multi-node failures.
- The selected event ordering is documented with live DuckDB 1.5.5 evidence.
- No production fix is merged without its failing regression test.

## Phase 1 — Make the pull feed lossless

**Goal:** A consumer can drain any retained snapshot completely.

### Engine changes

- Replace the ambiguous `Truncated`-only contract with a page carrying:
  - `Items`;
  - `NextCursor`;
  - `SnapshotId` or requested range;
  - `HasMore`;
  - source schema version;
  - an immutable source catalog id.
- Implement deterministic keyset pagination for the verified event ordering.
- Keep the existing bounded materialisation and cancellation behaviour.
- Read one snapshot at a time for CDC delivery and replication.
- Expose a snapshot manifest containing:
  - snapshot id and commit time;
  - schema version;
  - affected tables;
  - table lifecycle/schema events;
  - whether the snapshot contains row changes for each table.
- If DuckLake does not expose a sufficient historical affected-table manifest, derive and persist
  it when LakeHold commits, and document how externally committed snapshots are discovered.

### API changes

- Add a versioned CDC route following `PUBLIC-API.md` conventions, for example:

  ```text
  GET /api/v1/tenants/{tenant}/catalogs/{catalog}/cdc/snapshots/{snapshot}
  GET /api/v1/tenants/{tenant}/catalogs/{catalog}/cdc/snapshots/{snapshot}/tables/{schema}/{table}/changes?limit=&cursor=
  ```

- Return `problem+json` for invalid, expired, malformed, or cross-snapshot cursors.
- Keep the existing `/changes` route as a compatibility alias during one documented deprecation
  period. It must not claim that a truncated response is resumable unless it returns a usable next
  cursor.
- Add a lightweight endpoint to list snapshots after a durable consumer checkpoint.
- Include immutable source deployment/catalog identifiers. Do not use tenant slug or display
  catalog name alone as replication identity.

### Documentation

- Correct the one-snapshot, pagination, signature, deduplication, and timestamp descriptions in
  `README.md`, `ARCHITECTURE.md`, the in-app docs, and the incident runbook.
- Document the complete idempotency key as source id, catalog id, snapshot id, schema, table,
  row id, and change type/event ordinal.

### Exit criteria

- A 100,000-change single snapshot drains through bounded pages with an exact set comparison.
- Page sizes of 1, 2, 999, 1,000, and 10,000 produce identical complete results.
- Retrying any page cursor returns the same page.
- Invalid and stale cursors fail explicitly.
- Created, renamed, and dropped tables remain represented in the historical snapshot manifest.

## Phase 2 — Make webhook dispatch durable, ordered, and secure

**Goal:** Webhooks are operationally reliable notifications, including in a multi-node deployment.

### Control-plane model

Add a durable delivery entity rather than overloading `ChangeSubscription`:

- delivery id;
- subscription id;
- immutable source catalog id;
- snapshot id;
- envelope creation timestamp;
- status: pending, leased, delivered, abandoned;
- attempt count and last attempt;
- next attempt;
- lease owner and lease expiry;
- last status/error summary;
- delivered time;
- uniqueness on `(subscription_id, snapshot_id)`.

Add the appropriate EF migration and legacy control-plane import coverage.

### Producer and worker

- A producer discovers retained snapshots after each subscription cursor and inserts missing
  delivery rows idempotently.
- A bounded worker pool claims due deliveries from PostgreSQL.
- Enforce one in-flight delivery per subscription and ascending snapshot order.
- Regenerate the authoritative snapshot summary from immutable source state, but persist the
  delivery id and exact body so retry identity is byte-for-byte stable. Sign every attempt with a
  fresh timestamp.
- Advance subscription progress only after the matching delivery receives a 2xx and the result is
  persisted.
- Honour cancellation and a configured delivery timeout.
- Keep exponential backoff with jitter, cap, and an operator-visible next-attempt time.
- Handle `Retry-After` for 429 and 503 within configured bounds.
- Bound request bytes, response headers, redirects, active connections, and global/per-tenant
  concurrency.

### Signing contract

- Sign a versioned base containing timestamp, delivery id, and exact body bytes.
- Send and document:
  - `X-Lakehold-Delivery`;
  - `X-Lakehold-Timestamp`;
  - `X-Lakehold-Signature`;
  - a signature-version indicator if it is not encoded in the signature value.
- Extend `WebhookSigner.Verify` to validate syntax and provide a freshness check using
  `TimeProvider`.
- Use fixed-time signature comparison.
- Support overlapping old/new secrets during a bounded rotation window.
- Never include the secret, response body, target credentials, or submitted row data in logs.

### Egress security

- Default new subscriptions to HTTPS.
- Permit HTTP only through an explicit development/operator setting.
- Add an operator-controlled hostname/URI allowlist.
- Resolve and reject prohibited loopback, private, link-local, multicast, and metadata-service
  addresses before every delivery.
- Disable redirects by default. If enabled, bound them and revalidate every destination.
- Re-resolve on every attempt to limit DNS-rebinding exposure.
- Document an external network egress policy as the second boundary.

### Exit criteria

- Two or more API nodes deliver each logical subscription/snapshot once in normal operation.
- Killing a worker after the HTTP 2xx but before local completion causes a safe duplicate with the
  same delivery identity, never a skipped snapshot.
- A slow or dead receiver does not delay unrelated subscriptions beyond the configured concurrency
  bound.
- Private or redirected destinations cannot be reached.
- Signature freshness, rotation, and replay tests pass.

## Phase 3 — Couple retention and operations to CDC

**Goal:** Operators can run and recover CDC without direct database edits.

### Retention

- Register each durable consumer and its oldest required snapshot.
- Make expiry dry-run report blocking subscriptions and replicas.
- Make expiry apply refuse to cross the watermark.
- Provide an explicit abandon/re-bootstrap workflow instead of a force flag that silently loses
  changes.
- Test the watermark with several subscriptions and replicas at different positions.

### Subscription operations

Add capability-checked endpoints to:

- pause without deleting;
- resume;
- rotate signing secret;
- replay from a retained snapshot;
- abandon a failed historical range with explicit confirmation;
- inspect pending deliveries;
- retry immediately;
- test a destination without advancing the cursor.

Deleting a catalog must also remove or terminally close its subscriptions and pending delivery
rows. Deletion must not leave a permanently failing background job.

### Metrics and alerts

Publish at minimum:

- latest source snapshot;
- last completed snapshot per subscription;
- cursor lag in snapshots and wall time;
- pending and leased deliveries;
- delivery duration and result by status class;
- consecutive failures and next attempt;
- oldest required retention snapshot;
- payload size and truncation count;
- worker saturation and lease expiry count.

Update the monitoring and incident runbooks with executable checks and recovery steps.

### Exit criteria

- The documented CDC alerts can be implemented using exported metrics without scraping logs or
  querying PostgreSQL directly.
- Secret rotation preserves pending delivery identity and progress.
- Pause/resume and replay are covered by API, authorization, and browser/operator tests.
- Expiry cannot invalidate a healthy durable consumer.

## Phase 4 — Build the DuckDB replication foundation

**Goal:** Bootstrap and continuously maintain a dedicated DuckDB mirror.

### Projects

Add:

- `src/Lakehold.Client`: authenticated, cancellable .NET client for source identity, manifests,
  snapshot bootstrap, and cursor-paged CDC;
- `src/Lakehold.Replication`: transport-neutral replication planner, validation, checkpoint, and
  apply contracts;
- `src/Lakehold.Replicator`: hosted worker/CLI with the DuckDB target implementation;
- corresponding test projects under `tests/`.

Keep LakeHold-specific retention and subscription coordination in LakeHold. Keep target application
and checkpoint logic in the replication projects.

### Replica configuration

Configuration identifies:

- source LakeHold base URL;
- source tenant and immutable catalog id;
- source credential from an environment variable or secret provider;
- target DuckDB file;
- included schemas/tables;
- table mode: keyed or append-only;
- replication key columns for keyed tables;
- schema-change policy;
- poll interval and optional webhook wake-up configuration;
- verification and re-bootstrap policy.

Do not place plaintext credentials in the target database, source files, command-line arguments, or
logs.

### Target metadata

Create an internal schema such as `_lakehold_replication` with:

- source identity and configuration fingerprint;
- bootstrap snapshot;
- last fully applied snapshot;
- source schema version;
- apply start/completion times and last error;
- optional applied-delivery audit with bounded retention;
- target schema fingerprint and verification state.

Application tables must not be able to collide with this metadata schema.

### Initial bootstrap

1. Resolve and persist the immutable source/catalog identity.
2. Select a retained source snapshot `S`.
3. Validate every selected table:
   - supported type mapping;
   - configured unique replication key, or append-only mode;
   - no duplicate/null key that violates the declared policy.
4. Produce a consistent snapshot bootstrap artifact at exactly `S`.
5. Reuse the verified eject path where practical, extended to support a selected snapshot and a
   machine-readable schema/type manifest.
6. Create target schemas and tables using source logical types.
7. Load data through Parquet/Arrow in bulk rather than row-by-row JSON.
8. Independently verify table row counts and, where configured, content digests.
9. In one target transaction, record bootstrap completion and checkpoint `S`.
10. Begin CDC from `S + 1`.

The source must retain `S + 1` onward throughout bootstrap. Register the replica before exporting so
retention cannot race the bootstrap.

### Exit criteria

- Writes committed while bootstrap is running are captured after `S`, not lost or duplicated.
- A failed bootstrap never presents a completed checkpoint.
- Restarting bootstrap cleans or replaces only the target owned by that replica.
- Independent row counts and sampled/full digests agree at snapshot `S`.

## Phase 5 — Apply snapshots transactionally to DuckDB

**Goal:** Obtain exactly-once target effects from an at-least-once source.

For each next snapshot `N`:

1. Require target checkpoint `N - 1`; otherwise stop and diagnose a gap.
2. Fetch the snapshot manifest.
3. Reconcile and validate schema changes.
4. Drain every selected table's cursor-paged changes.
5. Pair update pre-images and post-images using the verified event identity.
6. Stage typed changes in temporary DuckDB tables.
7. Begin one DuckDB transaction.
8. Apply schema changes in the supported order.
9. Apply all table changes:
   - append-only insert;
   - keyed delete using the pre-image key;
   - keyed update, including key changes, using pre-image and post-image;
   - keyed insert.
10. Refuse missing, duplicate, or multiply matched keys.
11. Update `_lakehold_replication` checkpoint and schema fingerprint to `N`.
12. Commit.
13. Acknowledge source progress only after the commit succeeds.

If the process crashes before commit, DuckDB rolls back rows and checkpoint together. If it crashes
after commit but before acknowledgement, the worker reads checkpoint `N` and safely skips the
duplicate logical delivery.

### Schema policy

Support deliberately, in this order:

1. create table;
2. add nullable column;
3. add column with a reproducible default/backfill;
4. rename column/table when source history identifies it unambiguously;
5. widen compatible types;
6. drop column/table after explicit policy approval.

An incompatible type change, ambiguous rename, new non-null column without a usable default, or
unsupported complex type blocks the checkpoint. Never coerce and continue silently.

### Target ownership

- Hold one writer lock per target DuckDB file.
- Refuse two independently configured replicators for the same target/source pair.
- Detect target DDL or row drift using schema fingerprints and optional verification queries.
- Treat target application writes as unsupported; fail and require operator resolution or
  re-bootstrap.

### Exit criteria

- Insert, delete, update, key-changing update, and multi-table source transactions reproduce the
  source state after every snapshot.
- Forced crashes at every step leave either checkpoint `N - 1` with no effects from `N`, or
  checkpoint `N` with all effects from `N`.
- Replaying a committed snapshot is a no-op.
- Unsupported schema or identity conditions block before checkpoint advancement.

## Phase 6 — End-to-end verification and rollout

### Required automated scenarios

- 100,000 changes in one table and one snapshot.
- Hundreds of snapshots accumulated while the target is offline.
- Inserts, deletes, updates, key changes, duplicate non-key values, and null values.
- Multi-table transactions.
- Table and column create, add, rename, type change, drop, and recreate.
- Source and target restart.
- Network timeout before request, during body send, after receiver commit, and before
  acknowledgement.
- Two LakeHold dispatcher nodes and a worker lease takeover.
- Snapshot expiry racing a lagging replica.
- Source catalog backup/restore with identity validation.
- Target file backup/restore and resumed replication.
- Every supported logical type at boundary values.
- Deliberate target drift.
- Secret rotation.
- SSRF and redirect attempts.

For each snapshot, compare source-at-version and target:

- schema;
- row count;
- key set;
- deterministic content digest where the type supports canonical encoding.

### Delivery lanes

Add to `make test`:

- focused engine/API CDC tests;
- PostgreSQL multi-node delivery integration;
- local DuckDB replica integration;
- disposable end-to-end source/bootstrap/catch-up/restart verification;
- browser/operator coverage for subscription and replica state.

The authoritative suite must report zero skipped CDC/replication integration tests when its
PostgreSQL and storage dependencies are available.

### Rollout

1. Keep the replicator behind an explicit feature flag.
2. Run in verification-only mode against a disposable target.
3. Canary one append-only table.
4. Canary one keyed mutable table.
5. Exercise outage, retention, secret rotation, and re-bootstrap runbooks.
6. Publish SLOs only after observing representative load.
7. Mark the capability production-ready only after the exit gates below pass.

## Production exit gates

CDC and DuckDB replication are production-ready only when all are true:

- no retained change can be skipped because of page size, tied ordering, table lifecycle, retry,
  or concurrent dispatch;
- delivery retries have stable identity and verifiable freshness;
- destination policy passes the webhook security exit gate in
  `PRODUCTION-READINESS-ROADMAP.md`;
- a lagging registered consumer blocks destructive snapshot expiry;
- multi-node tests prove ordered subscription processing and lease recovery;
- bootstrap is consistent at an exact snapshot and independently verified;
- target changes and checkpoint commit atomically;
- keyless mutable tables and incompatible schema changes fail closed;
- pause, replay, rotation, lag, metrics, alerts, and re-bootstrap are documented and tested;
- the complete `make test` workflow passes with no skipped required integration lanes.

## Suggested implementation order

Execute in this order; do not start the replicator by coding around an incomplete feed:

1. Phase 0 regression tests and DuckLake event-ordering spike.
2. Phase 1 cursor-paged, one-snapshot pull protocol and snapshot manifest.
3. Phase 2 durable delivery/outbox, stable signing envelope, leases, and egress security.
4. Phase 3 retention watermark, operator APIs, metrics, and runbooks.
5. Phase 4 `.NET` client, target metadata, and exact-snapshot bootstrap.
6. Phase 5 transactional DuckDB apply engine.
7. Phase 6 end-to-end verification, canary, and production gate review.

## Expected files and ownership

Likely existing files to change:

- `src/Lakehold.Engine/Catalog/ChangeFeed.cs`
- `src/Lakehold.Engine/Catalog/LakehouseMaintenance.cs`
- `src/Lakehold.ControlPlane/Model/Entities.cs`
- `src/Lakehold.ControlPlane/Data/ControlPlaneContext.cs`
- `src/Lakehold.ControlPlane/Data/Migrations/`
- `src/Lakehold.Api/Cdc/CdcOptions.cs`
- `src/Lakehold.Api/Cdc/ChangeFeedDispatcher.cs`
- `src/Lakehold.Api/Cdc/WebhookSigner.cs`
- `src/Lakehold.Api/Contracts.cs`
- `src/Lakehold.Api/Endpoints/LakehouseEndpoints.cs`
- `tests/Lakehold.Engine.Tests/ChangeFeedTests.cs`
- `tests/Lakehold.Api.Tests/ChangeFeedDispatcherTests.cs`
- `tests/Lakehold.Api.Tests/WebhookSignerTests.cs`
- `web/lakehold-ui/e2e/phase2-operator.spec.ts`
- `README.md`
- `docs/ARCHITECTURE.md`
- `docs/PUBLIC-API.md`
- `docs/TESTING.md`
- `docs/OPERATIONS.md`
- `docs/runbooks/INCIDENT-RESPONSE.md`
- `docs/runbooks/MONITORING-AND-ALERTING.md`
- `web/lakehold-ui/src/app/docs.content.md`

Likely new areas:

- `src/Lakehold.Client/`
- `src/Lakehold.Replication/`
- `src/Lakehold.Replicator/`
- matching projects under `tests/`

Keep the first pull-protocol and dispatcher changes narrow. Do not combine them with the replication
worker in one review: the feed must prove lossless independently before the target depends on it.
