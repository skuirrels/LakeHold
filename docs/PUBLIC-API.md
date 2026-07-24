# The public control API

The plan for a public HTTP API to control **time travel** and the **whole lakehouse** — query,
schema, snapshots and rollback, maintenance, backup, eject, change feeds, provisioning, and schedules
— as one versioned, authenticated, documented surface.

Like [`AUTHENTICATION.md`](AUTHENTICATION.md), this is a specification and a running record. It is
written to be worked one step at a time: each step is independently shippable and testable and leaves
the product working. Nothing here contradicts an invariant in `AGENT.md`; where a rule already exists,
this document says how the API preserves it rather than restating why.

**This builds on `AUTHENTICATION.md`, which is the gate.** Tokens, provisioning (creating tenants and
catalogs), and the principal model are specified there and are a prerequisite for everything below —
a "public" API in front of an open door is not public, it is exposed. Auth and provisioning are not
re-specified here; this document references them and fills in the surface around them.

**That gate is now met.** Every phase of `AUTHENTICATION.md` has landed: tokens, instance-scoped
provisioning endpoints, the principal model, roles, and audit. What remains for this document is the
surface around them — versioning, `problem+json`, pagination, async jobs, and time travel. One caveat
carries forward: `RequireAuthentication` defaults to false, so a public surface must not assume every
request is authenticated.

## What exists today

Everything is under `/api` (unversioned) and errors are bare strings despite `AddProblemDetails` being
registered. Authentication and provisioning now exist (`AdminEndpoints`); the tenant is still a URL
segment, but it is validated against the credential rather than trusted.
`src/Lakehold.Api/Endpoints/`:

| Area | Route | Gap for a public API |
|---|---|---|
| Discovery | `GET /api/tenants` | Now scoped to the credential; still unversioned and unpaginated. |
| Provisioning | `POST`/`DELETE /api/tenants`, `…/catalogs` | Synchronous; no async job model. |
| Tokens | `POST`/`GET`/`DELETE …/{tenant}/tokens` | No pagination; no last-used tracking on the request path. |
| Query | `POST …/catalogs/{c}/query` | No time-travel option; result capped, no streaming variant. |
| Schema | `GET …/catalogs/{c}/schemas` | — |
| Time travel | `GET …/catalogs/{c}/snapshots?limit=` | **List only.** No as-of read, rollback, label, pin, or retention. |
| Maintenance | `POST …/catalogs/{c}/maintenance/{op}?apply=` | Synchronous; heavy ops block the request. |
| Backup | `GET …/backups`, `POST …/backups/restore` | Synchronous restore; no job model. |
| Eject | `POST …/eject`, `GET …/ejects` | Synchronous; no download. |
| CDC | `GET …/changes`, `…/subscriptions` | Solid; keep. |
| History | `GET …/{tenant}/history` | Principal now recorded; no cursor pagination. |
| Scheduling | `GET /api/maintenance/schedule` | Node-global, read-only; schedule is config-only. |

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
- **Cursor pagination** on every list: `?limit=&cursor=` → `{ "items": [...], "nextCursor": "…"|null }`.
  Replaces the bare `?limit=` clamps in place today.
- **Long-running operations are async jobs.** Eject, backup, restore, compact, and catalog-wide
  snapshot restore return `202 Accepted` with `{ "operationId": "…" }`; the caller polls
  `GET /api/v1/operations/{id}` → `{ status: queued|running|succeeded|failed, result?, error? }`. This
  keeps HTTP responsive, survives client disconnects, and gives one place to report progress. Fast,
  bounded operations (flush, a single-table restore) may stay synchronous.
- **`Idempotency-Key` header** is honoured on every mutating `POST` — eject, restore, snapshot
  restore, subscription create — so a retried request does not run twice.
- **Destructive stays dry-run.** Anything that drops history or data — `expire`, `cleanup`, snapshot
  restore, tenant/catalog delete — returns a **plan** by default and only commits with `?apply=true`
  (invariant 10). Restore never overwrites an existing catalog (invariant 12); deleting a catalog or
  tenant record detaches it and leaves DuckLake metadata and Parquet in place.
- **Secrets never appear in a response or a log.** Object-store and metadata credentials are set by
  *secret name* and never echoed (invariants 8, 13); the eject signing key and a subscription's secret
  are write-only (invariant 17).
- **OpenAPI is published in every environment** at `GET /api/v1/openapi.json`, not dev-only as today.

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

### Restore (roll a table back)

The API form of the workbench's **Restore…** action. It uses the verified single statement
`CREATE OR REPLACE TABLE t AS SELECT * FROM t AT (VERSION => n)`, which records a **new** snapshot
rather than erasing history — so the rollback is itself reversible and time travel stays intact.

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

The expiry window is hard-coded at seven days today. Make it policy, honouring pins:

```
GET  …/catalogs/{c}/retention
PUT  …/catalogs/{c}/retention   { "snapshotMaxAge": "30d", "keepPinned": true }
POST …/catalogs/{c}/snapshots/expire?apply=      # dry-run plan → apply; skips pinned snapshots
POST …/catalogs/{c}/files/cleanup?apply=         # unchanged semantics
```

`expire` and `cleanup` keep exactly today's destructive, dry-run-by-default behaviour (invariant 10);
the only change is that the window comes from policy and pinned snapshots are excluded from the plan.

---

## The rest of the lakehouse

| Capability | v1 surface | Change from today |
|---|---|---|
| **Provisioning** | `POST/GET/DELETE …/tenants`, `…/catalogs`, `…/tokens` | Specified in `AUTHENTICATION.md`. Delete detaches; never destroys data. |
| **Query (materialised)** | `POST …/query` | Add optional `asOf`; standardise errors. |
| **Query (streaming)** | `POST …/query:stream` → NDJSON | New. No row cap, by construction (rule 3). |
| **Schema** | `GET …/schemas`, `GET …/tables/{schema}.{table}` | Add single-table detail. |
| **Maintenance** | `POST …/maintenance/{flush\|compact\|backup}` | Non-destructive; `compact`/`backup` become jobs. |
| **Backup / restore** | `GET …/backups`, `POST …/backups/restore` | Restore becomes a job; keep never-overwrite. |
| **Eject** | `POST …/eject`, `GET …/ejects`, `GET …/ejects/{id}` | Job model; expose `verified`/`signed`/`complete`. |
| **CDC** | `GET …/changes`, `…/subscriptions` | Unchanged — already coherent. |
| **Schedules** | `GET/PUT …/catalogs/{c}/schedules` | New — schedules become API-settable and tenant-scoped, not config-only. |
| **Audit** | `GET …/{tenant}/history` | Add the principal (`AUTHENTICATION.md`, audit). |
| **Discovery** | `GET /api/v1/openapi.json`, `GET /api/v1/tenants` | OpenAPI in all environments; tenant list scoped by token. |

---

## Open questions

To settle during the step they block, not before starting:

1. **Streaming transport.** NDJSON is the simplest and matches the wire endpoint's row-at-a-time
   model; Server-Sent Events buys nothing here. Decide when `query:stream` is built.
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

## Order of work

Each step ships on its own. Steps are gated on `AUTHENTICATION.md` — nothing public is exposed before
auth closes the door.

| Step | Deliverable | Gate |
|---|---|---|
| 0 | `AUTHENTICATION.md` phases 1–3b (tokens, principal, provisioning) | The prerequisite; not part of this doc |
| 1 | `/api/v1` prefix, `problem+json` everywhere, cursor pagination, published OpenAPI | Conventions test suite; old routes still alias |
| 2 | `asOf` on `POST …/query` (read-only attachment) | As-of read returns historical rows; live query unchanged |
| 3 | Snapshot list filters + detail; `POST …/snapshots/{id}/restore` (dry-run/apply) | Single-table rollback verified; `snapshot_predates_table` returned |
| 4 | Labels, pins, retention policy; `expire` honours pins | Pinned snapshot survives an expire |
| 5 | `operations/{id}` resource; eject/backup/restore/compact become jobs | A slow eject returns `202` and completes out of band |
| 6 | `query:stream` (NDJSON); API-settable schedules; principal in history | Stream exceeds `MaxRowsPerResult` without truncation |

Steps 2–4 are the time-travel control surface this document exists for. Step 1 is what makes the whole
thing a public API rather than an internal one. Everything else is depth.
