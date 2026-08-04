# The public control API

The plan for a public HTTP API to control **time travel** and the **whole lakehouse** — query,
schema, snapshots and rollback, maintenance, backup, eject, change feeds, provisioning, and schedules
— as one versioned, authenticated, documented surface.

**Status date:** 3 August 2026

**Current boundary:** LakeHold v1.4.0 ships the canonical `/api/v1` server contract, retains `/api`
as a sunset-advertised compatibility alias, and publishes OpenAPI in production. Java, Go, .NET,
and Python 0.1.0 source clients are generated and tested from that frozen contract, but none of the
four SDKs is published to a public package registry.

Like [`AUTHENTICATION.md`](AUTHENTICATION.md), this is a specification and a running record. It is
written to be worked one step at a time: each step is independently shippable and testable and leaves
the product working. Nothing here contradicts an invariant in `AGENT.md`; where a rule already exists,
this document says how the API preserves it rather than restating why.

**This builds on `AUTHENTICATION.md`, which is the gate.** Tokens, provisioning (creating tenants and
catalogs), and the principal model are specified there and are a prerequisite for everything below —
a "public" API in front of an open door is not public, it is exposed. Auth and provisioning are not
re-specified here; this document references them and fills in the surface around them.

**That gate is now met.** Every phase of `AUTHENTICATION.md` has landed: tokens, instance-scoped
provisioning endpoints, the principal model, roles, and audit. Versioning, common problem responses,
bounded cursor pagination, durable operations, idempotency, capability discovery, and production
OpenAPI, NDJSON query/CDC streaming, snapshot keysets/detail/table preview, the shared SDK runtime
layer, and semantic compatibility automation ship in v1.4.0. Released-image SDK conformance covers
authenticated query streaming, tenant isolation, and cancellation in all four languages. Exhaustive
public-error fixtures and public package publication remain open.
The caveat that used to carry forward here — `RequireAuthentication` defaulting to false — is gone
with the switch. Every installation requires a credential on HTTP routes. A deployment may still
configure demo access, which serves a credential-less request as a reader scoped to one named
catalog.

## Implemented source boundary

The frozen contract currently contains 66 typed operations. It versions the existing access,
provisioning, token, system-settings, query-language, bounded-query, import, saved-query, schema,
storage, snapshot, maintenance, backup, eject, CDC, audit-history, scheduling, and managed-connector
families. Tenant and catalog route segments are checked against the resolved principal.

| Contract capability | Implemented source behavior | Remaining boundary |
|---|---|---|
| Versioning | Canonical `/api/v1`; `/api` rewrites to v1 and emits `Deprecation`, `Sunset`, and successor links | Remove the alias only after its documented compatibility window |
| Discovery and documentation | Anonymous capability discovery and production OpenAPI at `/api/v1/openapi.json`; stable unique operation ids, Bearer requirements, SDK reference/examples, and semantic merge-base compatibility gate | Expand error examples as exhaustive error conformance lands |
| Errors | Canonical failures are normalized to RFC 9457 with bounded handler-selected detail, stable `code`, and `requestId`; unhandled exceptions remain generic | Complete a conformance fixture for every public error code |
| Collections | Opaque, request-bound cursor envelopes with a 24-hour lifetime; snapshot history freezes a native snapshot-id keyset and CDC uses its native snapshot/row cursor | Replace remaining offset cursors only where a source-native ordering can provide stable traversal |
| Retryable mutations | Durable, content-type/query/payload-bound idempotency records on bounded control mutations; visible-ASCII keys are hashed at rest, bodies are capped at 1 MiB, and completed records are retained for seven days | Streaming imports and one-time token issuance are deliberately excluded because neither response can be safely replayed |
| Long work | Maintenance compact/backup, restore backup, and eject enqueue durable operations on v1; legacy calls retain their old synchronous behavior | Progress/cancellation and remaining future long-running resources |
| SDKs | Digest-pinned generation plus shared authentication, streaming query/CDC, typed problems, retry, pagination, idempotency, operation polling, cancellation, correlation, timeout, user-agent, redaction, docs/examples/matrices, and released-image query/isolation/cancellation conformance for all four languages | Complete exhaustive public-error conformance; publish, index, and clean-install public packages |

### Query-language contract

`GET /api/v1/query-languages` always returns SQL and adds only optional planners whose descriptor health
check succeeds. A client should populate its language selector from this endpoint rather than assume
that `csharp-linq` is installed. The catalog-aware starter endpoint is
`GET /api/v1/tenants/{tenant}/catalogs/{catalog}/query-languages/{language}/starter`; it returns source
generated against the current catalog shape plus its schema fingerprint.

The query request supports both forms:

```json
{ "sql": "select * from main.events limit 100" }
```

```json
{
  "language": "csharp-linq",
  "source": "Main.Events.Where(e => e.Revenue > 100)"
}
```

`language` defaults to `sql`; `source` takes precedence over the compatibility `sql` property. A
successful response includes `language`, `generatedSql`, and `diagnostics` in addition to the normal
tabular result. `generatedSql` is null for SQL. Invalid authored source returns structured planner
diagnostics with severity, stable `LINQnnn` code, message, and start/end line and column. An installed
but unavailable planner returns `503`; an unsafe planner response returns `502`. Neither failure
degrades the built-in SQL path.

Planner transport is not a public credential surface. The API sends source and a schema snapshot,
never catalog credentials, validates the returned single read-only command, and alone owns
attachment, authorization, execution, limits, telemetry, and history. See
[C# LINQ in the Workbench](LINQ_WORKBENCH.md).

### Streaming and time-travel contract

`POST /api/v1/tenants/{tenant}/catalogs/{catalog}/query:stream` accepts a SQL query request and emits
`application/x-ndjson`: one `schema` record, zero or more `row` records, and one `complete` record.
Only SQL is accepted on this transport; optional planners compile source before a stream is opened.
The server validates the statement as read-only before writing response headers, executes through a
structural read-only attachment, flushes each record, and honours disconnect cancellation. A failure
before streaming is RFC 9457; a failure after headers is a bounded `error` record. A client must not
treat EOF without `complete` as success.

`GET …/changes:stream` applies the same framing to a finite CDC window: `stream`, zero or more
`change`, then `complete`. When `toSnapshot` is omitted, the server freezes the newest snapshot once
before the first record and follows the native opaque CDC cursor until that bound is drained.

Snapshot history uses a protected keyset bound to tenant, catalog, and time filters. The cursor
carries a frozen upper native snapshot id and an exclusive lower position, so commits arriving during
traversal do not duplicate or displace rows. `GET …/snapshots/{snapshotId}` returns retained metadata;
`GET …/snapshots/{snapshotId}/table` returns a bounded, truncation-aware table preview using quoted
schema/table identifiers and DuckLake's exact snapshot version. It is not arbitrary historical SQL.

## Design rules

These apply to every endpoint below. They are the difference between "an API the workbench happens to
call" and a public one.

- **Versioned prefix `/api/v1`.** Everything new lives under it. The current unversioned routes remain
  as a deprecated alias for one release, then are removed.
- **Auth is the gate.** Every route resolves a `Bearer` token to `ILakeholdPrincipal`
  (`AUTHENTICATION.md`). Data routes require a *tenant* token, optionally narrowed to a catalog;
  provisioning requires an *instance* token, which cannot query. A route tenant or catalog that does
  not match the principal is a **404, not a 403**.
- **Errors are `application/problem+json`** (RFC 9457) with a stable machine `code` in an extension
  field — `catalog_not_found`, `snapshot_predates_table`, `restore_target_exists`, `read_only_catalog`,
  `instance_token_cannot_query`. The engine's verbatim message (today's response body) goes in
  `detail`; the `code` is what a client branches on.
- **Cursor pagination** on bounded lists: `?limit=&cursor=` →
  `{ "items": [...], "nextCursor": "…"|null }`. The cursor is opaque and bound to the route and
  non-cursor query parameters. It expires after 24 hours; clients must restart traversal rather than
  persist it as an asset identifier. The current generic cursor carries a protected offset, not a
  database snapshot: concurrent inserts or deletes can therefore move items across page boundaries.
  Database-backed handlers apply `Skip`/`Take` before materialisation; adapters that expose only a
  leading limit can re-read an earlier prefix during deep traversal. Source-native keyset/snapshot
  cursors remain a scale and consistency follow-up, not an implemented property.
- **Long-running operations are async jobs.** Eject, compact/backup maintenance, and backup restore
  return `202 Accepted` with `{ "operationId": "…" }`; the caller polls
  `GET /api/v1/operations/{id}` → `{ status: queued|running|succeeded|failed, result?, error? }`. This
  keeps HTTP responsive, survives client disconnects, and gives one place to report progress. Fast,
  bounded operations (flush, a single-table restore) may stay synchronous. Terminal operation
  records are retained for 30 days. A restore target that is a local filesystem path is safe only
  in a single-node deployment or when every worker sees the same genuinely shared mount at that
  path; the durable queue does not make node-local files shared.
- **`Idempotency-Key` header** is honoured on bounded retryable control mutations and binds the key
  to method, route, content type, query, and payload, so a retry replays the response and a changed
  request is refused. Both request and replay response are capped at 1 MiB. Streaming imports are
  excluded rather than buffering unbounded bodies. Token issuance is also excluded: replay would
  require persisting the one-time plaintext credential, which LakeHold explicitly refuses to do.
  Keys contain 16-128 visible ASCII characters. A completed response can be replayed for seven days;
  after that retention window the same key is new work. In-progress records are never expired
  automatically because an interrupted mutation is indeterminate and must fail closed.
- **Destructive stays dry-run.** Anything that drops history or data — `expire`, `cleanup`, snapshot
  restore, tenant/catalog delete — returns a **plan** by default and only commits with `?apply=true`
  (invariant 10). Restore never overwrites an existing catalog (invariant 12); deleting a catalog or
  tenant record detaches it and leaves DuckLake metadata and Parquet in place.
- **Secrets never appear in public responses or routine diagnostics.** Object-store and metadata
  credentials are set by *secret name* and never echoed (invariants 8, 13); the eject signing key and
  a subscription's secret are write-only (invariant 17). The documented first-start bootstrap is the
  explicit exception: when no token is supplied through `Lakehold__BootstrapToken` and the token
  store is empty, LakeHold emits the auto-minted instance provisioning token once to the operator
  log because it cannot be recovered later. Production deployments should inject that value through
  their secret manager so it is never logged.
- **OpenAPI is published in every environment** at `GET /api/v1/openapi.json`.
- **The API is the only SDK control boundary.** An SDK never connects to LakeHold's control-plane
  PostgreSQL database, DuckDB/DuckLake metadata, connector worker internals, or LINQ planner
  transport. Public DTOs and behavior belong to this contract, not to persistence entities.

## SDK contract

LakeHold has four first-party source SDK candidates over this API:

| Language | Source package | Current implementation |
|---|---|---|
| Java | `io.lakehold:lakehold-sdk` | Typed synchronous/asynchronous operations plus `LakeholdRuntime`; not on Maven Central |
| Go | `github.com/skuirrels/LakeHold/sdk/go` | Typed context-aware operations plus `runtime.go`; not release-tagged |
| .NET | `Lakehold.Sdk` | Typed cancellable async operations plus `Lakehold.Sdk.Runtime`; not on NuGet |
| Python | `lakehold-sdk` | Typed synchronous operations plus `lakehold_sdk.runtime`; not on PyPI |

The OpenAPI document is the single source for low-level operations and wire models. Generated code
will be wrapped by small handwritten convenience layers for language conventions; it does not
duplicate server authorization, validation, checkpoint, retry, or publication policy. The existing
`src/Lakehold.Client` remains a separate source-only replication client and is not the new
general-purpose `Lakehold.Sdk` candidate.

Every SDK must provide the same observable contract:

- bearer-token authentication without logging credentials;
- typed RFC 9457 errors carrying LakeHold error code, status, correlation id, and bounded detail;
- cursor iterators, idempotency keys, operation polling, `Retry-After` support, bounded retries, and
  explicit request timeouts. Go and .NET propagate request cancellation; Java exposes generated
  asynchronous-call cancellation and thread-interruptible runtime waits; Python's synchronous
  transport checks cooperative cancellation between retries/polls and relies on its request timeout
  to bound an in-flight call;
- streaming query/CDC consumption without silently materializing an unbounded result;
- API capability/version discovery, additive-field tolerance, SDK user-agent/version headers, and
  access to request correlation identifiers;
- coverage for access, tenants/catalogs/tokens, schemas and queries, saved queries, connectors and
  runs, dead letters and checkpoints, snapshots/time travel, CDC, maintenance, backup/eject,
  operations, and audit according to the caller's capability.

The shared source fixtures run through Java, Go, .NET, and Python and prove reliability plus
incremental NDJSON behavior appropriate to each transport model. The authenticated workflow pulls
an immutable released API image, provisions an isolated catalog-scoped reader, and verifies query
streaming, tenant isolation, and cancellation in every SDK. Exhaustive public-error fixtures remain.
An SDK is not released merely because generated code compiles: package signing/provenance,
reference documentation, examples, supported runtime versions, compatibility tables, and a clean
install test from the public registry are release gates.

## Invariants this API preserves

Stated as the API's obligations, so a reviewer can check each endpoint against them:

1. **Isolation is structural (invariant 4).** Access is chosen by *which catalog is attached* to the
   session, decided by the principal — never by parsing, filtering, or rewriting submitted SQL. The
   as-of read path below attaches the catalog at a snapshot; it does not rewrite the query.
2. **Capability is attachment (invariant 9, `AUTHENTICATION.md`).** A read-only token, or an as-of
   read, produces a read-only attachment. DuckDB refuses the write; there is no permission check for
   clever SQL to route around.
3. **The row cap belongs to materialising paths only (invariant 6).** `POST …/query` caps a JSON
   response; `POST …/query:stream` does not, and honours the same purpose by construction — rows are
   encoded to the socket and forgotten, exactly as the wire endpoint already does.
4. **Verified artifacts advertise their state (invariants 12, 16).** Eject and backup responses carry
   `verified`/`complete`; an unverified bundle is a failed request, not a successful one with a flag.

---

## Time travel

The focus, and the largest gap today. Four capabilities: read the past, list and inspect snapshots,
roll a table back, and govern how long history is kept.

### Read as-of

No SQL rewriting (rule 1): the catalog is attached **read-only at the snapshot**, then the caller's
plain SQL runs against that attachment. The provider already supports catalog-scoped `AsOfSnapshot`
and `AsOfTimestamp` ([`PROVIDER-FEEDBACK.md`](PROVIDER-FEEDBACK.md), gap 3, closed in 1.13.0), so this
is a session-provisioning change, not a query-parsing one.

```
POST /api/v1/tenants/{t}/catalogs/{c}/query
{ "sql": "SELECT …", "asOf": { "version": 42 } }        # or { "timestamp": "2026-07-20T09:00:00Z" }
```

`asOf` is optional; absent, the query runs against the live catalog as it does today. Present, the
attachment is read-only regardless of the token, because the past cannot be written.

### Snapshots

```
GET /api/v1/tenants/{t}/catalogs/{c}/snapshots?since=&until=&cursor=&limit=
GET /api/v1/tenants/{t}/catalogs/{c}/snapshots/{id}
```

The list gains time-range filters and cursor pagination over today's `?limit=`. Each snapshot carries
its id, commit time, schema version, commit message, and — new — its `label` and `pinned` flag
(below). The detail endpoint adds the set of tables changed by that snapshot, drawn from
`ducklake_table_changes`.

### Restore table data

The workbench already uses the unversioned single-table form:

```
POST /api/tenants/{t}/catalogs/{c}/snapshots/{id}/restore-table
{ "schema": "main", "table": "events", "apply": false }
```

It returns live and historical row counts plus shared, current-only, and historical-only columns.
Apply stages historical rows before deleting anything and inserts through the current table definition
inside one labelled transaction. This preserves current defaults, nullability, and constraints; a
failure rolls the delete back. Apply requires the current snapshot id returned by the plan, so an
intervening commit is refused until the caller reviews again. `CREATE OR REPLACE TABLE AS SELECT` is
deliberately not used because it discards that table metadata.

The versioned API generalises the same contract:

```
POST /api/v1/tenants/{t}/catalogs/{c}/snapshots/{id}/restore
{ "tables": ["main.events"], "apply": false }
```

- `apply: false` (default) returns a plan: the target tables and their row deltas. `apply: true`
  commits.
- Omitting `tables` targets every base table in the catalog, and becomes an async job (it is N
  statements). A single named table may run synchronously.
- Refusals are `problem+json`: `snapshot_predates_table` when a table did not exist at that snapshot
  (a real case — a table created after snapshot *n* cannot be rolled back to it), and
  `read_only_catalog` when the token or catalog forbids writes.

### Label and pin

Real control over which history survives:

```
PUT    …/snapshots/{id}/label   { "label": "pre-migration" }
POST   …/snapshots/{id}/pin     # exempt this snapshot from expiry
DELETE …/snapshots/{id}/pin
```

DuckLake labels only its own commits, via the commit message, so arbitrary-snapshot labels and pins
need a small control-plane table keyed by `(catalogName, snapshotId)` — the same home as change
subscriptions. This is a deliberate decision recorded in Open questions, not an assumption.

### Retention

The expiry window is deployment configuration (`Lakehouse:SnapshotRetention`, seven days by
default), and active CDC subscriptions/consumers can extend it. A future tenant policy also needs to
honour pins:

```
GET  …/catalogs/{c}/retention
PUT  …/catalogs/{c}/retention   { "snapshotMaxAge": "30d", "keepPinned": true }
POST …/catalogs/{c}/snapshots/expire?apply=      # dry-run plan → apply; skips pinned snapshots
POST …/catalogs/{c}/files/cleanup?apply=         # unchanged semantics
```

`expire` and `cleanup` keep exactly today's destructive, dry-run-by-default behaviour (invariant 10).
Today, expiry reports and refuses to cross the next snapshot needed by an active webhook or
registered pull consumer. The remaining v1 change is tenant-level policy plus pinned snapshots.

### CDC and durable pull consumers

Current unversioned routes:

```text
GET    …/cdc/snapshots/{snapshot}/changes?schema=&table=&limit=&cursor=
GET    …/changes?schema=&table=&fromSnapshot=&toSnapshot=&limit=&cursor=
GET    …/subscriptions
POST   …/subscriptions
PUT    …/subscriptions/{id}
DELETE …/subscriptions/{id}
GET    …/cdc/consumers
POST   …/cdc/consumers
PUT    …/cdc/consumers/{id}/checkpoint
DELETE …/cdc/consumers/{id}
```

The snapshot route is the authoritative replication surface. Its opaque cursor is bound to the
requested schema, table, and snapshot and orders by snapshot, row id, then explicit change-type
ordinal. `nextCursor: null` means the table is drained. A durable consumer registers its committed
target checkpoint and advances it monotonically only after its own target transaction commits;
deletion is an explicit abandonment of the retention claim.

`PUT …/subscriptions/{id}` supports pause/resume, write-only secret replacement, replay from a
retained snapshot, and retry-now. Webhook retries reuse the durable delivery id and exact body; each
attempt carries a fresh timestamp and signature so receiver freshness checks remain valid after a
long outage.

---

## The rest of the lakehouse

| Capability | v1 surface | Change from today |
|---|---|---|
| **Provisioning** | `POST/GET/DELETE …/tenants`, `…/catalogs`, `…/tokens` | Specified in `AUTHENTICATION.md`. Delete detaches; never destroys data. |
| **Query (materialised)** | `POST …/query` | Add optional `asOf`; standardise errors. |
| **Query (streaming)** | `POST …/query:stream` → NDJSON | New. No row cap, by construction (rule 3). |
| **Saved queries / views** | `…/saved-queries`, `…/{id}/{execute\|publish\|unpublish}` | Keep optimistic revisions and read-only execution; add cursor pagination and standard errors. |
| **Schema** | `GET …/schemas`, `GET …/tables/{schema}.{table}` | Add single-table detail. |
| **Maintenance** | `POST …/maintenance/{flush\|compact\|backup}` | Non-destructive; `compact`/`backup` become jobs. |
| **Backup / restore** | `GET …/backups`, `POST …/backups/restore` | Restore becomes a job; keep never-overwrite. |
| **Eject** | `POST …/eject`, `GET …/ejects`, `GET …/ejects/{id}` | Job model; expose `verified`/`signed`/`complete`. |
| **CDC** | snapshot-scoped cursor feed, subscriptions, consumers | Add `/api/v1` alias and `problem+json`; preserve cursor and watermark semantics. |
| **Schedules** | `GET/PUT …/catalogs/{c}/schedules` | New — schedules become API-settable and tenant-scoped, not config-only. |
| **Audit** | `GET …/{tenant}/history` | Add the principal (`AUTHENTICATION.md`, audit). |
| **Discovery** | `GET /api/v1/openapi.json`, `GET /api/v1/tenants` | OpenAPI in all environments; tenant list scoped by token. |

---

## Open questions

To settle during the step they block, not before starting:

1. **Streaming transport — decided.** NDJSON matches the wire endpoint's row-at-a-time model and is
   implemented for query and CDC. Server-Sent Events is not used because it adds no useful semantics.
2. **Where labels and pins live.** A control-plane table keyed by `(catalogName, snapshotId)` is the
   recommendation — it survives backup, restore, and eject without those three having to reason about
   it, exactly the argument `AUTHENTICATION.md` makes for keeping access rules out of DuckLake. The
   alternative is waiting for native DuckLake support. Blocks the label/pin step.
3. **Catalog-wide restore atomicity.** Restoring every table to snapshot *n* is N statements. Whether
   they run in one transaction (all-or-nothing, but a long single writer hold) or table-by-table (each
   its own snapshot, resumable) is a real trade to make when the job is built.
4. **Per-principal quotas.** `AUTHENTICATION.md` open question 3 notes `MaxRowsPerResult` and the
   statement timeout are per-node. If quotas become per-principal, `query` and `query:stream` are
   where they bind.

## Delivery status and order of work

Each step ships on its own. Steps are gated on `AUTHENTICATION.md` — nothing public is exposed before
auth closes the door.

| Step | Status | Deliverable | Gate |
|---|---|---|---|
| 0 | **Shipped** | `AUTHENTICATION.md` phases 1–3b (tokens, principal, provisioning) | The prerequisite; not part of this doc |
| 1 | **Implemented in source** | `/api/v1` prefix, common RFC 9457 normalization, bounded cursor pagination, production OpenAPI | Convention and contract tests pass; old routes still alias |
| 2 | **Not started** | `asOf` on `POST …/query` (read-only attachment) | As-of read returns historical rows; live query unchanged |
| 3 | **Partial** | Snapshot list filters/keyset + detail/table preview; `POST …/snapshots/{id}/restore` (dry-run/apply) | Filters, detail, and exact-snapshot table preview are implemented; catalog-wide restore remains |
| 4 | **Not started** | Labels, pins, retention policy; `expire` honours pins | Pinned snapshot survives an expire |
| 5 | **Implemented in source** | `operations/{id}` resource; eject, compact/backup maintenance, and backup restore become jobs | v1 returns `202`; persisted worker completion and expired-lease indeterminate state are tested |
| 6 | **Partial** | `query:stream` (NDJSON); API-settable schedules; principal in history | Query and CDC streams plus principal history exist; schedule mutation remains |
| 7 | **Partial** | Generate Java, Go, .NET, and Python SDK candidates from the reviewed contract | Source clients and streaming fixtures exist; authenticated workflow is ready, but full released-server conformance remains |
| 8 | **Partial** | Publish signed SDK packages, reference documentation, examples, and compatibility policy | Documentation, examples, matrices, changelog, signing/provenance workflow, and clean-install gate exist; public publication has not run |

Steps 2–4 are the time-travel control surface this document exists for. Step 1 is what makes the whole
thing a public API rather than an internal one. The source clients now have executable verification,
but steps 7–8 are not complete until the full shared suite and public release gates pass. Everything
else is depth.
