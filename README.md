# LakeHold

**An open-source lakehouse platform built on DuckDB and DuckLake, with a .NET backend and an
Angular frontend.**

[**lakehold.dev**](https://lakehold.dev)

> Your lakehouse, your bucket, your VPC. A serverless-feeling DuckDB warehouse that runs on your
> own infrastructure, stores every byte as open Parquet you can read without us, and speaks .NET
> natively.

LakeHold is the self-hostable answer to MotherDuck. It provides a tenant-aware query service, a
catalog, and a web query IDE over [DuckLake](https://ducklake.select) — an open table format that
stores tables as ordinary Parquet files and metadata as ordinary SQL.

The PostgreSQL control plane, credentials, and audit records are tenant-scoped. Compute remains
in-process DuckDB: a query runs on one worker, while additional API/worker nodes can serve other
queries against the same PostgreSQL metadata and object storage. The remaining shared-artifact and
background-job gates for an adversarial multi-tenant service are tracked in the
[`production-readiness roadmap`](docs/PRODUCTION-READINESS-ROADMAP.md).

---

## How LakeHold compares

LakeHold trades managed elasticity and platform maturity for infrastructure control, open storage,
and first-class .NET integration. It is not the right choice for everyone; this matrix uses the same
claims as the live [`/compare`](https://lakehold.dev/compare) page so the README does not present a
simpler story than the product site.

<!-- compare-matrix:start -->
| | LakeHold | MotherDuck | ClickHouse | Snowflake / Databricks |
|---|---|---|---|---|
| Deployment | Self-hosted anywhere, incl. air-gapped | Hosted catalog; local or cloud compute | Self-hosted or ClickHouse Cloud | Hosted service only |
| Where your data lives | Your disk or object store, under your control | Managed storage or your own bucket | Your disks, or their cloud | Their account, or external tables |
| Accounts, SSO, permissions | OIDC browser sign-in, scoped API tokens, three roles, in-product member and token administration; no row policies | Accounts, SSO, org roles | Users, roles, row policies | Mature RBAC, SSO, lineage |
| Table format | DuckLake — plain Parquet + SQL catalog | DuckLake — same open format | MergeTree, proprietary on disk | Delta / Iceberg, now genuinely open |
| Read data without the product | Yes — tested, see exit path | Yes, DuckLake is open | Export required | Yes, via Iceberg / Delta readers |
| Other engines read it live | Eject or export today; Iceberg REST planned | Direct with BYO compute; catalog remains hosted | Its own protocols; export for others | Yes — via their catalog endpoints |
| Time travel | Yes — query your data from an earlier point in time | Yes | No first-class equivalent | Yes, mature |
| Verified, signed export | One call — row-count attested and signed | Manual export; nothing attests it | Manual export | Manual unload; nothing attests it |
| Change data capture | Built in — typed feed + signed webhooks | Limited; not exposed directly | Kafka engine or external tooling | Yes — CDF / streams, mature |
| Managed ingestion | REST/gRPC plus PostgreSQL/HubSpot incremental adapters shipped in v1.3.0; broad catalogue pending | Managed and partner connectors | Broad integrations and managed ClickPipes | Extensive first-party and partner connectors |
| AI / MCP | Authenticated MCP; read tools + operator-gated writes | Managed MCP with sandboxed compute | Open-source and managed remote MCP | Managed AI and agent platforms with MCP |
| BI tools (Power BI, Tableau) | Postgres wire protocol; Power BI blocked on type loading | Postgres endpoint; connector for older tools | Native connectors and JDBC/ODBC | First-class connectors everywhere |
| Maintenance control | Explicit, dry-run by default | Automatic, not exposed | Explicit merges and TTLs | Automatic, partly exposed |
| .NET / EF Core | One model for app and lake; client package pending | Community drivers; Python/JS first | Solid ADO.NET client, no ORM story | JDBC/ODBC; .NET is second-class |
| Scale ceiling | Scale out workers; each query stays on one node | Elastic, scales past a node | Clustered, petabyte-scale | Effectively unlimited |
| Concurrent writers | PostgreSQL-backed DuckLake metadata; worker-local execution | Managed | High concurrency | High concurrency |
| Operational burden | You run it | None | High if self-hosted | Low |
| Licence | Apache-2.0 | Proprietary | Apache-2.0; Cloud proprietary | Proprietary |
| Cost shape | A VM and a bucket | Free Lite; Business $250/org/mo + usage | Free self-hosted; Cloud pay-as-you-use | Usage / credit based; enterprise spend varies |
<!-- compare-matrix:end -->

Pricing and tiers were checked in July 2026 and move constantly; treat the cost row as a shape, not
a quote. The website also gives the fuller “choose LakeHold when / choose them when” case for each
alternative. Every LakeHold claim in the matrix is mapped to executable evidence in the browser,
backend, integration, or deployment test suites.

Catalog isolation is structural — a session can only reference the catalog attached to it — and the
layer deciding *which* tenant a caller is now exists too: the credential names the tenant and the URL
segment is validated against it. One caveat, and it is the whole caveat: the *application* default
for `Lakehold:Auth:RequireAuthentication` is **false**, so a bare `dotnet run` accepts token-less
requests and trusts the route. `compose.production.yaml` sets it to true, so the deployment path is
closed by default and only a hand-rolled one can be left open.

The trade is deliberate: **elasticity and zero-ops for control, openness, and .NET integration.**
Full analysis, including where MotherDuck is the better choice, in
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

---

## Quick start

Requirements: Docker. (The .NET 10 SDK and Node 20+ only if you want to run the app on the host.)

```bash
cp .env.example .env
docker compose up
```

# → Open <http://localhost:5399>

That is the website. Everything else is optional detail.

| | URL |
|---|---|
| **Workbench — the website** | **<http://localhost:5399>** |
| API | <http://localhost:5200> |
| Traces | <http://localhost:16686> |
| MinIO console | <http://localhost:59001> |

The first run takes a few minutes — it restores NuGet packages and runs `npm ci` inside the
containers — and seeds a `demo` workspace with an `analytics` catalog of 250,000 events and 5,000
customers, so the workbench is usable the moment it loads. Later starts are fast, and source is
bind-mounted so saving a file hot-reloads in place. The API development image bakes in DuckDB's
required signed extensions, including Excel workbook support, so the running process never needs
to download them.

New here? The [getting-started guide](web/lakehold-ui/src/app/docs.content.md) walks every feature —
what it does, how to reach it, and what it is for. That one Markdown file is the single source for
both this copy and the in-app page served at <http://localhost:5399/docs>.

Production database and local/S3/GCS/Azure configuration is documented in
[PostgreSQL and Parquet storage](docs/POSTGRES-AND-STORAGE.md).

```bash
docker compose down -v    # stop and discard the data
```

### Running it in production

`compose.yaml` above is the **development** stack: stock SDK images, your source bind-mounted, and a
file watcher. It is not a deployment. `compose.production.yaml` is, and it is the whole install —
one file naming published images, so a deployment host needs neither a checkout nor a compiler:

```bash
curl -O https://raw.githubusercontent.com/skuirrels/LakeHold/main/compose.production.yaml
export LAKEHOLD_CONTROL_PLANE='<postgres connection string>'
export LAKEHOLD_DUCKLAKE_METADATA='<postgres connection string>'
docker compose -f compose.production.yaml up -d
```

→ the private Workbench on <http://localhost:8080/workbench>. `/` redirects there; the public
landing, comparison, documentation, and provider pages are not served by a standard installation.

Then read the bootstrap token out of the log and open the Workbench, which walks the rest:

```bash
docker compose -f compose.production.yaml logs api | grep -i bootstrap
```

SQL is always built in. In a standard private production deployment, add the isolated C# LINQ
planner by setting `LAKEHOLD_LINQ_PLANNER_KEY` and adding `--profile linq` to the Compose command.
Removing that profile returns the same deployment to SQL-only operation. The separate public
evaluation target, `make demo`, enables the profile and generates its internal credential
automatically. See [C# LINQ in the Workbench](docs/LINQ_WORKBENCH.md).

Pin `LAKEHOLD_TAG` to a release rather than tracking `latest`, or a redeploy silently moves you to
whatever was tagged since. From a checkout, `make deploy` pulls and restarts in one step, and
`make status`, `make logs`, `make stop`, and `make backup-state` cover the rest. Nothing in either
file removes the state volume.

**Deploying from source instead** — a fork, a patch, or a commit that has not been released — adds
the build override, which supplies a build context for each image:

```bash
docker compose -f compose.production.yaml -f compose.build.yaml up -d --build
```

`make production` is that path as a repeatable private deployment: it refuses to run if tracked
files have been edited on the host, pulls `--ff-only` so a deployment can never invent a merge
commit, and rebuilds before it restarts anything — a broken build leaves the current containers
serving traffic. `--wait` means it exits non-zero unless both healthchecks pass, so a container that
starts and immediately crashes fails the deploy rather than reporting success.

| | Development (`compose.yaml`) | Production (`compose.production.yaml`) |
|---|---|---|
| Install | Clone the repository | One file, published images |
| Images | .NET SDK + Node, ~1 GB each | Published output only — 416 MB API, 63 MB web |
| Authentication | Off unless you turn it on | **Required** unless you turn it off |
| Source | Bind-mounted, hot reloads | Not present in the image |
| Runs as | root | Non-root (`app`, `nginx`) |
| Browser UI | Angular dev server and public pages | **Private Workbench only** |
| API port | Published on `:5200` | **Not published** — reached through the Workbench origin |
| Demo data | Seeded on first run | Never |
| State | Named volume | Named volume, `/var/lib/lakehold` |

Worth knowing:

- **The API is not exposed to the host.** nginx serves the Workbench and proxies `/api` on the same
  origin, which removes CORS from the deployment and leaves one published port. Publish the API
  yourself if something outside the compose network needs it.
- **The public pages are demo-only.** The image contains the prerendered landing, comparison,
  documentation, and provider pages, but the production nginx mode cannot serve them: `/` redirects
  to `/workbench` and public or unknown routes return 404. `make demo` explicitly selects the
  website mode that exposes those pages.
- **Demo seeding is off.** `Lakehold:SeedDemoData` defaults to the environment, so a production node
  never invents a `demo` tenant holding 250,000 rows. Schema initialisation still runs — that is
  what creates tables added since a database was first initialised.
- **PostgreSQL is required but not bundled in the standard production stack.** It is both the
  shared application control plane and the default DuckLake metadata catalog. Point the two
  connection strings at managed or operator-owned PostgreSQL; they may use separate databases/users
  for least privilege. The separate evaluation-only demo overlay includes a private PostgreSQL
  container so `make demo` is self-contained.
- **Parquet storage is independent.** A local path remains supported for a deliberate single-node
  or shared-filesystem deployment. S3/S3-compatible, GCS, and Azure Blob/ADLS profiles are the
  recommended multi-node choices.
- **The image is architecture-pruned.** Publishing for the target RID drops the Windows and macOS
  DuckDB natives that a portable publish would ship — 940 MB down to 416 MB — and `TARGETARCH`
  keeps it correct on arm64 hosts.
- **The images come from GHCR**, built and pushed by `.github/workflows/release.yml` on a `v*` tag,
  for amd64 and arm64. Neither pays for emulation: both Dockerfiles run their build stage on the
  builder's own architecture, the API cross-publishing by `TARGETARCH` and the website emitting
  static files that have no architecture at all. `LAKEHOLD_REGISTRY_NAMESPACE` repoints them at a
  fork's namespace or a mirror.
- **Authentication is required here**, unlike the development stack: this file sets
  `Lakehold__Auth__RequireAuthentication` to `true`. The application default stays `false` so a fresh
  checkout runs token-lessly, but that default is wrong for anything with a published port. Set
  `LAKEHOLD_REQUIRE_AUTH=false` to go back to trusting the route — knowing that it means anyone who
  reaches `:8080` is every tenant.
- **The first credential comes out of the log.** A node with no tokens mints an instance-scoped
  bootstrap token on first start and logs it once. Open the Workbench and it asks for it, then
  trades it for a token that can actually read — provisioning and querying are deliberately different
  capabilities. [Authentication](#authentication) has the same three steps as `curl`, if you would
  rather script it.
- **Upgrading a deployment that predates this** will find authentication suddenly enforced. That is
  the point, but it is a breaking change for a node whose clients hold no tokens: issue them first,
  or set `LAKEHOLD_REQUIRE_AUTH=false` for the one deploy that bridges the gap.
- **Back the state volume up with `make backup-state`, then copy it off-host.** It is the control
  plane, catalog metadata, local Parquet, backup generations, and eject bundles — everything in the
  default stack that cannot be rebuilt. The archive is a file copy, so stop the stack first when it
  must be application-consistent. Never extract an archive over a populated volume; the
  [disaster-recovery runbook](docs/runbooks/DISASTER-RECOVERY.md) restores into an empty volume and
  defines the validation required before traffic returns.

#### Operational runbooks

Production operation starts with [`docs/OPERATIONS.md`](docs/OPERATIONS.md), which defines the
supported topology, ownership, production entry gate, routine checks, deployment, rollback, and
evidence handling. The actionable runbooks are:

- [incident response](docs/runbooks/INCIDENT-RESPONSE.md);
- [disaster recovery](docs/runbooks/DISASTER-RECOVERY.md);
- [monitoring and alerting](docs/runbooks/MONITORING-AND-ALERTING.md).

They distinguish liveness from readiness, catalog backup from full-state recovery, and same-volume
artifacts from off-host disaster protection. Assign on-call ownership, configure an OTLP backend,
export consistent state backups, and complete a clean-node restore drill before accepting production
traffic.

### Demo deployment overlay

Demo mode is intentionally absent from the customer production file and `.env.example`. It is the
only deployment target that enables the public website. To run that separate evaluation deployment,
add its overlay:

```bash
make demo
```

This refuses tracked local changes, pulls the current branch with `--ff-only`, then builds the API,
UI, and isolated C# LINQ compiler images before starting them in website mode. C# LINQ is enabled in
the Workbench by default. `make demo` generates and supplies the internal planner credential, so no
feature key needs to be configured or retained by the operator. The site listens on port `8080` by
default; use `LAKEHOLD_PORT=8081 make demo` when that port is already occupied.

`compose.demo.yaml` owns the website mode, demo seeding, authentication, the read-only visitor
scope, and a private PostgreSQL 17 service whose metadata survives restarts in the
`lakehold_demo-postgres-data` volume. It defaults to `demo/analytics`;
`LAKEHOLD_DEMO_TENANT` and `LAKEHOLD_DEMO_CATALOG` can point the overlay at a different seeded
catalog. `LAKEHOLD_DEMO_POSTGRES_PASSWORD` can replace the evaluation-only database password before
the volume is first created. This does not disable authentication: credential-less requests receive
a reader scoped to that one catalog, while a valid operator token retains its normal capabilities.
Production deployments should continue to use managed or operator-owned PostgreSQL through the two
connection-string variables above.

### Running the app on the host instead

A faster inner loop, if you have the SDK and Node installed. Start only the backing services, then
the two halves:

```bash
docker compose up -d postgres minio minio-bucket jaeger

dotnet run --project src/Lakehold.Api      # API on :5200
npm start --prefix web/lakehold-ui         # website on :5399
```

Same URLs either way. The dev server proxies `/api`, `/mcp`, and MCP authorization metadata to
`NG_API_URL`, which compose sets to the API container and which falls back to `localhost:5200` when
nothing sets it. DuckDB caches the same extension set, including `excel`, under the host user's
normal `~/.duckdb/extensions` directory.

---

## Architecture

Two planes, split by whether the workload has a model:

```
Angular Workbench (SQL + optional C# LINQ) ──REST──▶ Lakehold.Api
                                 │
                 ┌───────────────┴───────────────┐
                 ▼                               ▼
         CONTROL PLANE                     DATA PLANE
    ControlPlaneContext                  LakeContext
    EF model · migrations                model-less · dynamic SQL
    tenants · catalogs · saved source    Duckling sessions · planned SQL · views
                 └──── DuckDB.EFCoreProvider ────┘
```

SQL is executed directly. Optional language planners receive only authored source and a catalog
schema snapshot, return parameterized SQL, and never receive catalog credentials. The isolated C#
LINQ planner uses the provider's non-executing command-plan and exact named-replay APIs; the API
validates and executes the returned read plan. See
[`docs/LINQ_WORKBENCH.md`](docs/LINQ_WORKBENCH.md).

A **Duckling** is one tenant's compute session: an in-memory DuckDB instance with that tenant's
DuckLake catalog attached, under a memory limit and thread budget. Isolation is structural — a
tenant can only reference the catalog attached to its own session, so cross-tenant access is
prevented by attachment scope rather than by inspecting submitted SQL.

Both planes run on the same provider, split by whether they have a model rather than by
dependency. The data plane is a model-less `DbContext` serving arbitrary SQL through the provider's
streaming dynamic-query API — see [`docs/PROVIDER-FEEDBACK.md`](docs/PROVIDER-FEEDBACK.md) for how
that changed between provider 1.12.0 and 1.13.0.

| Project | Role |
|---|---|
| `Lakehold.Engine` | Duckling sessions, catalog introspection, maintenance |
| `Lakehold.ControlPlane` | EF Core model: tenants, catalogs, saved queries, audit |
| `Lakehold.Api` | Minimal-API HTTP surface |
| `Lakehold.Querying` | Credential-free query-language and plan contracts |
| `Lakehold.Linq.Compiler` | Optional isolated C# LINQ-to-DuckDB planner |
| `Lakehold.ServiceDefaults` | Health, resilience, and OpenTelemetry defaults |
| `web/lakehold-ui` | Angular 22 workbench and landing page |

---

## The exit path

The open-format claim is testable, so we test it. After running the demo:

```bash
duckdb -c "SELECT event_type, count(*), sum(revenue)
           FROM read_parquet('src/Lakehold.Api/.lakehold/data/analytics/main/events/*.parquet')
           GROUP BY 1"
```

No DuckLake extension, no LakeHold, no metadata catalog — just Parquet. See
[`docs/EXIT-PATH.md`](docs/EXIT-PATH.md).

One caveat worth knowing: **DuckLake inlines small commits into the metadata catalog rather than
writing Parquet immediately.** A two-row insert produces no data files. Run the **Flush**
maintenance operation (or `ducklake_flush_inlined_data`) to force them out. LakeHold surfaces this
as a first-class control precisely because the guarantee depends on it.

Flush and compaction commit their snapshots with a `lakehold maintenance: …` message, so the snapshot
history distinguishes what the platform did from what you did. A run that changes nothing commits
nothing, so scheduled maintenance leaves no empty entries behind.

---

## Eject: the exit path as one call

The glob above is the *quick* demonstration. It is also, on its own, **not a safe migration**:
DuckLake deletes are merge-on-read sidecars, updates leave superseded rows in place, and inlined
commits are not in Parquet at all. Copy those files and you resurrect deleted rows and duplicate
updated ones.

An **eject** does it correctly, in one call:

```bash
curl -X POST localhost:5200/api/tenants/demo/catalogs/analytics/eject \
     -H 'Content-Type: application/json' -d '{"includeHistory":true}'
```

It re-materialises every table *through* the catalog, so deletions and updates are applied, inlined
rows are included, and DuckLake's internal columns are gone. The result is ordinary Parquet:

```
ejects/analytics/20260720T230438Z/
├── MANIFEST.json                       # attestation, written last
├── data/main/events.parquet            # clean, reader-agnostic
├── data/main/customers.parquet
└── catalog/ducklake_*.parquet          # history, when includeHistory
```

Every file is counted back through the plain Parquet reader and compared to the catalog's own count.
**A mismatch aborts the eject before the manifest is written**, so a bundle that claims to be
complete has been verified rather than merely finished. The manifest carries per-table row counts and
SHA-256 digests, and an HMAC signature when a key is configured:

```jsonc
{
  "Lakehouse": {
    "EjectRoot": "./.lakehold/ejects",       // sibling of the data root, like backups
    "EjectSigningKey": ""                    // set to sign manifests; a secret, never logged
  }
}
```

Verify a bundle with no LakeHold and no .NET in the loop:

```bash
duckdb -c "SELECT count(*) FROM read_parquet('…/data/main/events.parquet')"
sha256sum …/data/main/events.parquet     # compare against MANIFEST.json
```

Because it only reads, an eject never mutates the catalog and works on a read-only share.

---

## Change data capture, without a pipeline

DuckLake already records what each snapshot changed, so LakeHold exposes it directly rather than
asking you to run Debezium and Kafka to get it back out. Two surfaces, same source.

**Pull** — typed change pages:

```bash
curl "localhost:5200/api/tenants/demo/catalogs/analytics/cdc/snapshots/9/changes?schema=main&table=events&limit=1000"
```

```jsonc
{
  "fromSnapshot": 9, "toSnapshot": 9, "truncated": true,
  "nextCursor": "eyJWZXJzaW9uIjoxLC4uLn0",
  "changes": [
    { "snapshotId": 9, "rowId": 3, "changeType": "update_preimage",  "row": { "id": 4, "status": "new" } },
    { "snapshotId": 9, "rowId": 3, "changeType": "update_postimage", "row": { "id": 4, "status": "shipped" } }
  ]
}
```

Pass the opaque `nextCursor` back unchanged until it is `null`. It is bound to the schema, table,
and snapshot range; changing any of those inputs fails explicitly rather than silently resuming in
the wrong feed. The older `/changes` route remains a compatibility alias and returns the same
cursor.

**Push** — a signed webhook per new snapshot:

```bash
curl -X POST localhost:5200/api/tenants/demo/catalogs/analytics/subscriptions \
     -H 'Content-Type: application/json' \
     -d '{"endpointUrl":"https://example.com/hook","secret":"at-least-16-characters"}'
```

```jsonc
{
  "Lakehold": {
    "Cdc": {
      "Enabled": true,
      "PollInterval": "00:00:15",      // upper bound on delivery latency
      "MaxChangesPerTable": 1000,      // beyond this, payload sets truncated and you pull the rest
      "MaxSnapshotsPerSubscriptionPerSweep": 100,
      "MaxConcurrentSubscriptions": 4,
      "DeliveryTimeout": "00:00:30",
      "LeaseDuration": "00:01:00",
      "MaxBackoff": "00:30:00"         // a dead endpoint costs one request per cap, not per poll
    }
  }
}
```

Worth knowing:

- **The range is inclusive at both ends.** A consumer through snapshot `L` reads from `L + 1`.
  Verified on DuckDB 1.5.4 — getting this wrong duplicates or drops a window.
- **An update is two rows**, `update_preimage` and `update_postimage` sharing a `rowId`. Take net
  effect, or diff them.
- **Delivery is at-least-once with a durable cursor.** Deliveries advance one snapshot at a time and
  the cursor moves only after a 2xx. PostgreSQL stores one delivery identity, exact body, attempt
  state, and expiring lease for each subscription/snapshot pair. Multiple API nodes may dispatch;
  optimistic claims keep normal operation single-delivery while a crashed worker can be taken over.
  A crash after the receiver commits can still produce a safe duplicate.
- **Payloads are HMAC-SHA256 signed** over the exact
  `v1.<timestamp>.<delivery-id>.<body-bytes>` base. Verify `X-Lakehold-Signature-Version`,
  `X-Lakehold-Signature`, `X-Lakehold-Timestamp`, and `X-Lakehold-Delivery`, reject stale
  timestamps, and deduplicate by delivery id. The id and body are stable across retries; every
  attempt has a fresh timestamp and signature. The secret is write-only and never logged.
- **The pull feed is authoritative.** A truncated webhook only inlines a prefix. Drain the matching
  snapshot through the cursor-paged pull route before acknowledging downstream work.
- **Destinations are policy checked.** Production defaults require HTTPS and reject loopback,
  private, link-local, multicast, and metadata-service addresses after DNS resolution on every
  attempt. The socket is pinned to the approved address and redirects are disabled. `AllowHttp` and
  `AllowUnsafeDestinations` are development-only escape hatches; `AllowedHosts` can narrow egress
  further.
- **Retention follows durable consumers.** Replicas register a checkpoint under `/cdc/consumers`.
  Snapshot expiry reports and refuses to cross any live subscription—including a paused one—or an
  active consumer watermark.
  Pause/resume, secret rotation, replay, and retry-now use `PUT …/subscriptions/{id}`; deleting a
  consumer explicitly abandons its retention claim.

### Replicate LakeHold into DuckDB

The first-party worker bootstraps selected tables at one exact source snapshot and then applies each
later source snapshot to a dedicated DuckDB file. Target rows and
`_lakehold_replication.checkpoints` commit in the same DuckDB transaction, so an at-least-once
source produces exactly-once target effects.

```bash
export LAKEHOLD_REPLICA_TOKEN='lh_…'
dotnet run --project src/Lakehold.Replicator -- docs/examples/duckdb-replica.json
```

The example config selects each table as either `keyed` (with declared unique key columns) or
`appendOnly`. Keyed inserts, deletes, updates, and key changes are supported. Append-only tables
stop on an update or delete. This first slice deliberately fails closed on schema changes,
unsupported nested/complex types, missing or duplicate target keys, snapshot gaps, and target
schema drift; resolve those conditions or re-bootstrap rather than coercing ahead. The source must
be a LakeHold/DuckLake catalog—an arbitrary plain DuckDB database has no snapshot change feed to
consume.

---

## Authentication

The credential names the tenant; the URL segment is validated against it rather than trusted. A token
belongs to one tenant, may be narrowed to a single catalog, and carries a role — `owner`, `editor`,
or `reader`.

To get signed in for the first time, or to put Keycloak (or any OIDC provider) behind the Workbench,
follow [`docs/IDENTITY-PROVIDER-SETUP.md`](docs/IDENTITY-PROVIDER-SETUP.md) — it is the step-by-step
version of this section, including the claim mappers an external provider has to emit.

**The application default is off**, so a fresh checkout still runs token-lessly. The production
compose file turns it on, and any deployment with a published port should:

```jsonc
{ "Lakehold": { "Auth": { "RequireAuthentication": true } } }
```

The separate `compose.demo.yaml` overlay configures `Lakehold:Auth:DemoTenant` and
`Lakehold:Auth:DemoCatalog`. A credential-less request then receives a synthetic reader principal
scoped to exactly that catalog; writes, maintenance, restore, eject, token administration, and
subscription changes remain forbidden. Presented credentials are always validated and take
precedence over demo access.

A node with no tokens mints an instance-scoped one at start-up and logs it **once**. That token
provisions tenants, catalogs, and other tokens, and deliberately cannot read data — so a leaked admin
credential is a visible provisioning problem, not a silent data breach.

The workbench does this for you: open the site, paste the bootstrap token when it asks, name a
workspace and a catalog, and it mints the token that can query them and shows it once. An
administrator can later issue, review, and revoke least-privilege credentials under **System
Settings → API tokens**, including catalog scope, role, and optional expiry. Revocation closes MCP
and the public API immediately. The same three bootstrap steps by hand:

```bash
docker compose -f compose.production.yaml up -d
docker compose -f compose.production.yaml logs api | grep -i bootstrap

# The production stack does not publish the API: nginx serves the site and proxies /api on the same
# origin, so provisioning goes to :8080. On the development stack it is localhost:5200 instead.
API=http://localhost:8080/api
ADMIN='lkh_admin_…'

curl -X POST $API/tenants -H "Authorization: Bearer $ADMIN" \
     -H 'Content-Type: application/json' -d '{"slug":"acme","displayName":"Acme"}'
curl -X POST $API/tenants/acme/catalogs -H "Authorization: Bearer $ADMIN" \
     -H 'Content-Type: application/json' -d '{"name":"analytics"}'
curl -X POST $API/tenants/acme/tokens -H "Authorization: Bearer $ADMIN" \
     -H 'Content-Type: application/json' -d '{"name":"bi","role":"reader"}'
```

Worth knowing:

- **A token is shown once** and stored only as a SHA-256 hash with its public prefix, so it cannot be
  recovered from the API or the database. `Lakehold__BootstrapToken` overrides the minted one where a
  platform injects credentials; the supplied Compose files map `LAKEHOLD_BOOTSTRAP_TOKEN` from
  `.env` to that setting.
- **A token defaults to `reader`**, as does one naming an unrecognised role, so a typo costs read
  access rather than granting everything. Pass `owner` or `editor` deliberately. Tokens issued before
  roles existed remain owners — the column's default preserves what they already were.
- **Capability is attachment, not policy.** A `reader` token's catalog is attached read-only, so a
  write fails in the engine rather than in a check that clever SQL might route around — the same
  reasoning as the isolation model.
- **A refusal is a 404, not a 403.** Reaching a tenant or catalog your credential does not name
  returns "not found", because a 403 would confirm it exists.
- **Maintenance, restore, and eject are owner operations**; querying is a reader's.
- **Revocation closes both surfaces** when the wire endpoint runs on the token store
  (`Lakehold:PgWire:AllowTokenAuthentication`), rather than leaving a BI tool connected on a
  credential the API already refuses.
- **OIDC** covers humans through an authorization-code + PKCE Workbench login. Set an authority and
  client id, register `https://<lakehold-host>/auth/callback`, and map the configured system-admin
  claim to people allowed to manage MCP client credentials. Their session is an HttpOnly cookie
  protected by keys shared through PostgreSQL; provider tokens never reach browser JavaScript.
  Leave the authority unset and the path stays off, preserving air-gapped operation.

The full design, including what is deliberately still open, is in
[`docs/AUTHENTICATION.md`](docs/AUTHENTICATION.md).

---

## The PostgreSQL wire endpoint

LakeHold speaks the PostgreSQL wire protocol, so a client that already speaks Postgres connects to a
catalog with no `.mez` file, driver, or plugin involved. It is off by default — it opens a database
port — and enabling it without a password refuses to start.

> **Power BI does not connect yet.** A client that loads the server's type catalogue on connect
> sends four statements in one message and expects four result sets back. Multi-statement messages
> now work; the catalogue those statements read still does not line up with DuckDB's. `psql`,
> DBeaver's PostgreSQL driver, and Npgsql with `NoTypeLoading` work today. What was measured, what
> remains, and the three known remedies are in [`docs/POSTGRES-WIRE.md`](docs/POSTGRES-WIRE.md).

```jsonc
{
  "Lakehold": {
    "PgWire": {
      "Enabled": true,
      "Port": 5433,
      "MaxRows": 0                 // 0 = unbounded; rows stream to the socket
    }
  }
}
```

```bash
# Lakehold__PgWire__Password lives in .env
psql "host=localhost port=5433 dbname=analytics user=demo"
```

The mapping is the part to remember: **user is the tenant, database is the catalog.**

| Postgres | LakeHold |
|---|---|
| `Username=demo` | tenant slug |
| `Database=analytics` | catalog name |

### Connecting a client

**DBeaver** — new connection, PostgreSQL, host `localhost`, port `5433`, database the *catalog* name,
username the *tenant* slug, password from `.env`. On the driver properties tab set **SSL** off; the
endpoint declines TLS and DBeaver retries in plaintext only if it is not required.

**.NET / Npgsql** — the connection string the test suite uses, and the one to copy:

```text
Host=localhost;Port=5433;Database=analytics;Username=demo;Password=…;
SSL Mode=Disable;Server Compatibility Mode=NoTypeLoading
```

`NoTypeLoading` is not optional. Without it Npgsql tries to read the server's type catalogue and gets
nothing back — the same thing that currently blocks Power BI.

**Power BI** — not yet, per the note above. When the type-loading shim lands, the flow is *Get Data →
PostgreSQL database*, server as `host:5433`, database as the catalog, credentials on the **Database**
tab rather than Windows — and **clear "Use Encrypted Connection"**, which the connector enables by
default and which this endpoint has no TLS to satisfy. Expect Import mode to behave before
DirectQuery does: DirectQuery generates parameterised queries, and bound parameters are still
refused.

Worth knowing:

- **The 10,000-row ceiling does not apply here.** It bounds a JSON response that has to be built in
  memory before it is sent; a wire connection encodes each row and writes it, so results stream
  instead. Handing a BI tool a silent prefix of a table would be worse than a slow query.
- **Every statement goes through the same seam as an HTTP query**, so it resolves the same tenant
  check, queues on the same session gate, and lands in the same query history — including the
  introspection statements a BI tool sends on its own initiative.
- **Writes complete with a real count.** `INSERT 0 12`, `UPDATE 7`, `DELETE 3` — the number a driver
  hands back from `ExecuteNonQuery`. The provider's dynamic path cannot report one, so counted DML
  runs as a non-query; see [`docs/POSTGRES-WIRE.md`](docs/POSTGRES-WIRE.md).
- **No session state survives between statements.** Temporary tables and `SET` values do not persist,
  because each statement resolves a fresh session. Invisible to BI traffic, not to `psql` users.
- **Bound parameters are refused**, not guessed at, and `BEGIN`/`COMMIT` are acknowledged rather than
  executed. Both are honest stubs — see [`docs/POSTGRES-WIRE.md`](docs/POSTGRES-WIRE.md).
- **Credentials are per tenant**, so one tenant's password does not open another's catalog. A single
  shared `Password` still works for single-tenant deployments, where the distinction is meaningless.
- **TLS is supported** — point `TlsCertificatePath` at a `.pfx` or a PEM pair, and set `RequireTls`
  to refuse clients that will not encrypt. Without a certificate the endpoint serves plaintext, as
  before.
- **Type-catalogue loading is the open blocker**, not a vague "untested" caveat. A client that reads
  `pg_type` at connection time gets an empty result from DuckDB and gives up. That is what stops
  Power BI, and it is fixable in the shim rather than in DuckDB.

## Backup, restore, and scheduling

The metadata catalog is the one part of a DuckLake deployment that is not already an open format, so
LakeHold exports it to Parquet on a schedule and can rebuild a catalog from that export.

This is a catalog-recovery mechanism, not a complete operational recovery plan. It does not contain
the control plane, API-token plaintext, table data, configuration, or secrets. In the packaged
production API the backup root is currently resolved beneath the same state volume as the catalog,
so a whole-volume failure loses both unless the operator has exported a consistent off-host state
archive. See [Disaster recovery](docs/runbooks/DISASTER-RECOVERY.md) for the recovery matrix,
approved procedures, validation, and drill cadence.

```jsonc
{
  "Lakehouse": {
    // The packaged API resolves local data/, backups/, and ejects/ as siblings under this root.
    // Separately bound roots are not preserved by the current host; see the caveat below.
    "StateRoot": "./.lakehold",
    "BackupRetainCount": 7
  },
  "Lakehold": {
    "Maintenance": {
      "Enabled": true,
      "FlushCron": "0 0/15 * * * ?",   // bounds permanently unrecoverable data
      "BackupCron": "0 0 * * * ?",
      "CompactCron": "0 30 2 * * ?",   // I/O heavy, so off-peak
      "NodeId": "",                    // defaults to the machine name
      "LeaseDuration": "00:30:00"
    }
  }
}
```

Worth knowing:

- **Only non-destructive operations are scheduled.** `expire` and `cleanup` stay manual and
  dry-run-by-default. Automating an irreversible deletion would undo the safety the rest of the
  product argues for.
- **Restore never overwrites.** It writes a new metadata file and refuses if the target exists.
  Re-pointing a tenant at the result is a separate, deliberate step.
- **A backup with no manifest is refused.** If an export died partway and the missing table is
  `ducklake_delete_file`, deleted rows silently return on restore.
- **PostgreSQL metadata restores into a DuckDB file**, so this is an exit path from the catalog
  database and not just a copy of it.
- **The engine supports object-store backup roots, but the packaged API does not yet preserve
  separately bound roots.** `Program.cs` currently resolves backup, metadata, data, and eject roots
  under `Lakehouse:StateRoot`. The production path therefore needs an off-host state archive today.
  When a custom host wires an object-store backup root, DuckDB cannot prune it; set a lifecycle rule
  on the prefix.
- **Multi-node deployments take a lease per job per catalog**, so every node firing the same cron
  does not run the same sweep. New catalogs use PostgreSQL metadata; a legacy local-metadata catalog
  cannot be opened by two nodes.

### Tests

The full testing strategy and feature-by-feature coverage matrix live in
[`docs/TESTING.md`](docs/TESTING.md).

```bash
dotnet test Lakehold.slnx
npm run test:unit --prefix web/lakehold-ui
npm run test:e2e --prefix web/lakehold-ui
```

The backup tests run against real services rather than mocks, because the failures they guard
against — object stores having no directories, PostgreSQL attaching nothing queryable behind the
catalog — are invisible to the type system. Bring them up with compose:

```bash
cp .env.example .env      # first time only
docker compose up -d      # PostgreSQL + MinIO, and creates the test bucket
dotnet test Lakehold.slnx # 0 skipped

docker compose down -v    # stop and discard the data
```

`compose.yaml` also creates the `lakehold-test` bucket, which the S3 tests need and do not create
themselves; without it they fail against a running MinIO rather than skipping cleanly.

### Configuration

Configuration lives in source control. Secrets live in `.env`. The dividing line is whether the
value would be identical for every developer:

| Kind | Where | Examples |
|---|---|---|
| Application settings | `src/Lakehold.Api/appsettings*.json` | telemetry endpoint, CDC and maintenance schedules, row ceilings |
| Service ports, users, database names | `compose.yaml` (inline defaults) | `55439`, `59000`, `lakehold`, `lakeholdmeta` |
| **Secrets** | **`.env`** *(gitignored)* | service passwords, S3 keys, the eject signing key |

Keeping `.env` short is the point: the smaller it is, the easier it is to see that everything in it
genuinely had to stay out of the repository. [`.env.example`](.env.example) is the checked-in
template — `cp .env.example .env`.

`.env` is loaded automatically by the API at start-up, by the test suite, and by compose for
variable substitution, so nothing needs exporting into your shell — which also means the IDE test
runner sees the same configuration as the terminal.

Three properties worth knowing:

- **Real environment variables always win.** Loading never overwrites a value already set, so a
  container variable or CI secret is never shadowed by a stale local file.
- **A missing `.env` is a no-op.** Deployments configure through their platform's environment or
  secret store; nothing depends on a file in source control.
- **The integration-test variables stay in `.env` even where they are not secret.** The tests read
  the process environment directly rather than `IConfiguration`, and an endpoint only means anything
  next to the credential it authenticates with.

Use the .NET double-underscore separator for nested keys in the environment —
`Lakehouse__EjectSigningKey` binds to `Lakehouse:EjectSigningKey`.

---

## Status

Working today: a CodeMirror Workbench with built-in SQL and an optional isolated C# LINQ language,
catalog completion, generated-SQL visibility, diagnostics, separate language buffers, a catalog
explorer and result grid, catalog-scoped reusable queries with
optimistic revisions and explicit publication as DuckLake views, query history and audit, unified
data history with snapshot drill-down, historical row browsing, bounded change comparison and an
atomic dry-run/confirm table-data restore that preserves the current table definition, maintenance
operations (flush, compact, expire, cleanup — destructive ones
dry-run by default, with explicit confirmation), scheduled maintenance with multi-node leasing,
catalog backup and restore for both local-file and PostgreSQL metadata, **verified and signed eject
bundles**, **CDC via a typed pull API and signed outbound webhooks**, a managed connector platform
with REST/gRPC full snapshots, PostgreSQL/HubSpot incremental adapters, commit-fenced checkpoints,
replay-safe upsert, retries/dead letters, schema contracts, external secret references, quality
gates, and retained run lineage, a PostgreSQL wire endpoint for
`psql`, DBeaver, and Npgsql (Power BI still needs the type-catalogue shim), read-only cross-catalog
attach, tenant-scoped credentials and audit, and demo seeding.

The connector API, versioned in-process adapter contract, security model, and deliberately narrow
four-adapter catalogue are documented in
[`docs/CONNECTORS.md`](docs/CONNECTORS.md). The remaining ingestion, governance, semantic, and BI
work needed for the broader Enterprise Data Platform position is tracked in the
[`Enterprise Data Platform roadmap`](docs/ENTERPRISE-DATA-PLATFORM-ROADMAP.md).
The product position, current capability map, use cases, and explicit limitations are documented in
the [`Enterprise Data Platform overview`](docs/ENTERPRISE-DATA-PLATFORM.md) and published at
[`/enterprise-data-platform`](https://lakehold.dev/enterprise-data-platform).

Also shipped: **authentication and tenant identity** — API tokens with tenant and catalog scoping,
instance-scoped provisioning and bootstrap, read-only capability enforced by attachment, per-statement
audit, the PostgreSQL wire endpoint on the same token store (so revocation closes both surfaces),
OIDC, owner/editor/reader roles, and an authenticated **MCP server for AI agents** with read tools,
resources, OAuth protected-resource metadata, and operator-gated writes. Development enables MCP by
default, and the instance credential can change its live controls under **System Settings** without
restarting the API. See
[`docs/AUTHENTICATION.md`](docs/AUTHENTICATION.md) and [`docs/MCP.md`](docs/MCP.md); note that HTTP
API enforcement is opt-in per deployment via `Lakehold:Auth:RequireAuthentication`, while MCP always
requires a credential.

Also implemented in source: a canonical `/api/v1` control surface, production OpenAPI, common
errors/pagination/idempotency/durable-operation conventions, and generated Java, Go, .NET, and
Python SDK candidates. Their source reliability layers now share typed errors, bounded
`Retry-After` retries, lazy pagination, idempotency helpers, durable-operation polling,
transport-appropriate cancellation, request timeouts, correlation ids, user agents, and
additive-field tolerance. The packages are
not published, streaming helpers and the remaining released-server conformance are still open, and
the existing `Lakehold.Client` remains the separate replication-only client.

Next: implement streaming query/CDC resources and SDK helpers, then complete released-server
conformance and approved package publication;
an Iceberg REST Catalog endpoint so Spark, Trino, and Snowflake read LakeHold tables live with no
export; and read-only share links. See
[`docs/PUBLIC-API.md`](docs/PUBLIC-API.md) and the
[`EDP roadmap`](docs/ENTERPRISE-DATA-PLATFORM-ROADMAP.md).

Later: continuous exit attestation — the verified eject running on a schedule, so "you can leave" is
a signed and dated artifact rather than an on-demand call. And embedded Duckling — the same lakehouse
running in-process in a .NET app and graduating to the server unchanged.

Not planned: dual local/cloud execution.

---

## Contributing

Contributions are welcome — see [`CONTRIBUTING.md`](CONTRIBUTING.md). A one-time
[Contributor License Agreement](CLA.md) is required before your first pull request can be
merged; it keeps the project's licensing options open while leaving you the copyright to your
work.

---

## Licence

Apache-2.0. Built on [DuckDB](https://duckdb.org), [DuckLake](https://ducklake.select), and
[DuckDB.EFCoreProvider](https://github.com/skuirrels/DuckDB.EFCoreProvider).

[lakehold.dev](https://lakehold.dev)
