# Lakehold

**An open-source lakehouse platform built on DuckDB and DuckLake, with a .NET backend and an
Angular frontend.**

[**lakehold.dev**](https://lakehold.dev)

> Your lakehouse, your bucket, your VPC. A serverless-feeling DuckDB warehouse that runs on your
> own infrastructure, stores every byte as open Parquet you can read without us, and speaks .NET
> natively.

Lakehold is the self-hostable answer to MotherDuck. It provides a tenant-aware query service, a
catalog, and a web SQL IDE over [DuckLake](https://ducklake.select) — an open table format that
stores tables as ordinary Parquet files and metadata as ordinary SQL.

The control plane, credentials, and audit records are tenant-scoped. The current node-local session
and artifact layout is not yet safe for a shared, adversarial multi-tenant deployment when two
tenants use the same catalog name. Treat the present release as trusted evaluation or a one-tenant
production profile until the isolation gates in the
[`production-readiness roadmap`](docs/PRODUCTION-READINESS-ROADMAP.md) are complete.

---

## Why

| | MotherDuck | Lakehold |
|---|---|---|
| Deployment | Hosted only | **Self-hosted: laptop, VM, k8s, air-gapped** |
| Storage | Managed; BYO bucket on paid tiers | **Your bucket, always** |
| Format | DuckLake | DuckLake (same) |
| .NET / EF Core | — | **First-class, one model for app and lake** |
| Time travel | ✅ | ✅ |
| CDC / change feeds | Limited | **Typed pull API + signed webhooks, no Debezium or Kafka** |
| Provable exit | — | **Verified, signed eject bundles** |
| Maintenance controls | Automatic, hidden | **Explicit: flush, compact, expire, cleanup — dry-run by default** |
| Elastic scale-out | ✅ | Bounded by node size |
| Zero operations | ✅ | You run it |
| Dual execution | ✅ | Not replicated |
| Accounts, SSO, permissions | ✅ | **API tokens, OIDC, and roles — on in the production stack** |

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
bind-mounted so saving a file hot-reloads in place.

New here? The [getting-started guide](web/lakehold-ui/src/app/docs.content.md) walks every feature —
what it does, how to reach it, and what it is for. That one Markdown file is the single source for
both this copy and the in-app page served at <http://localhost:5399/docs>.

```bash
docker compose down -v    # stop and discard the data
```

### Running it in production

`compose.yaml` above is the **development** stack: stock SDK images, your source bind-mounted, and a
file watcher. It is not a deployment. `compose.production.yaml` is, and it is the whole install —
one file naming published images, so a deployment host needs neither a checkout nor a compiler:

```bash
curl -O https://raw.githubusercontent.com/skuirrels/LakeHold/main/compose.production.yaml
docker compose -f compose.production.yaml up -d
```

→ the website on <http://localhost:8080>

> **Until this repository has its first `v*` tag there is nothing in the registry to pull**, and the
> command above fails on a missing manifest. Deploy from source in the meantime — the build override
> below does exactly that.

Then read the bootstrap token out of the log and open the site — the workbench walks the rest:

```bash
docker compose -f compose.production.yaml logs api | grep -i bootstrap
```

Pin `LAKEHOLD_TAG` to a release rather than tracking `latest`, or a redeploy silently moves you to
whatever was tagged since. From a checkout, `make deploy` pulls and restarts in one step, and
`make status`, `make logs`, `make stop`, and `make backup-state` cover the rest. Nothing in either
file removes the state volume.

**Deploying from source instead** — a fork, a patch, or a commit that has not been released — adds
the build override, which supplies a build context for each image:

```bash
docker compose -f compose.production.yaml -f compose.build.yaml up -d --build
```

`make production` is that path as a repeatable deploy: it refuses to run if tracked files have been
edited on the host, pulls `--ff-only` so a deployment can never invent a merge commit, and rebuilds
before it restarts anything — a broken build leaves the current containers serving traffic. `--wait`
means it exits non-zero unless both healthchecks pass, so a container that starts and immediately
crashes fails the deploy rather than reporting success.

| | Development (`compose.yaml`) | Production (`compose.production.yaml`) |
|---|---|---|
| Install | Clone the repository | One file, published images |
| Images | .NET SDK + Node, ~1 GB each | Published output only — 416 MB API, 63 MB web |
| Authentication | Off unless you turn it on | **Required** unless you turn it off |
| Source | Bind-mounted, hot reloads | Not present in the image |
| Runs as | root | Non-root (`app`, `nginx`) |
| Website | Angular dev server | Prerendered bundle behind nginx |
| API port | Published on `:5200` | **Not published** — reached through the site's own origin |
| Demo data | Seeded on first run | Never |
| State | Named volume | Named volume, `/var/lib/lakehold` |

Worth knowing:

- **The API is not exposed to the host.** nginx serves the site and proxies `/api` on the same
  origin, which removes CORS from the deployment and leaves one published port. Publish the API
  yourself if something outside the compose network needs it.
- **The marketing pages are prerendered.** `ng build` emits real HTML for `/`, `/compare`, and
  `/docs` so crawlers and link unfurlers do not have to execute JavaScript to see the content; the
  authenticated workbench stays client-rendered and `noindex`. Adding a public route means adding it
  to `public/sitemap.xml` and giving it a `data.seo` description in `app.routes.ts`. Unmatched paths
  fall back to `index.csr.html`, never to a prerendered page.
- **Demo seeding is off.** `Lakehold:SeedDemoData` defaults to the environment, so a production node
  never invents a `demo` tenant holding 250,000 rows. Schema initialisation still runs — that is
  what creates tables added since a database was first initialised.
- **Nothing else is in the file.** No PostgreSQL, no MinIO: Lakehold defaults to local-file metadata
  and a local data path, so neither is required. Point configuration at whatever you already run
  rather than at a single-container database this stack would imply was production-grade.
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
  bootstrap token on first start and logs it once. Open the site and the workbench asks for it, then
  trades it for a token that can actually read — provisioning and querying are deliberately different
  capabilities. [Authentication](#authentication) has the same three steps as `curl`, if you would
  rather script it.
- **Upgrading a deployment that predates this** will find authentication suddenly enforced. That is
  the point, but it is a breaking change for a node whose clients hold no tokens: issue them first,
  or set `LAKEHOLD_REQUIRE_AUTH=false` for the one deploy that bridges the gap.
- **Back the state volume up with `make backup-state`.** It is the catalog, the Parquet, the backup
  generations, and the eject bundles — the only thing in the stack that cannot be rebuilt. The
  archive is a file copy, so stop the stack first if it has to be restorable with certainty; the
  catalog-consistent tools are Lakehold's own backup and eject. Restore is deliberately manual:
  ```bash
  make stop
  docker run --rm -v lakehold_lakehold-state:/state -v "$PWD":/archive alpine \
    sh -c 'rm -rf /state/* && tar xzf /archive/<archive>.tar.gz -C /state'
  make deploy
  ```

### Demo deployment overlay

Demo mode is intentionally absent from the customer production file and `.env.example`. To run the
separate evaluation deployment, add its overlay:

```bash
make demo
```

This builds the API and web images from the current checkout before starting them. The site listens
on port `8080` by default; use `LAKEHOLD_PORT=8081 make demo` when that port is already occupied.

`compose.demo.yaml` owns demo seeding, authentication, and the read-only visitor scope. It defaults
to `demo/analytics`; `LAKEHOLD_DEMO_TENANT` and `LAKEHOLD_DEMO_CATALOG` can point the overlay at a
different seeded catalog. This does not disable authentication: credential-less requests receive a
reader scoped to that one catalog, while a valid operator token retains its normal capabilities.

### Running the app on the host instead

A faster inner loop, if you have the SDK and Node installed. Start only the backing services, then
the two halves:

```bash
docker compose up -d postgres minio minio-bucket jaeger

dotnet run --project src/Lakehold.Api      # API on :5200
npm start --prefix web/lakehold-ui         # website on :5399
```

Same URLs either way. The dev server proxies `/api` to `NG_API_URL`, which compose sets to the API
container and which falls back to `localhost:5200` when nothing sets it.

---

## Architecture

Two planes, split by whether the workload has a model:

```
Angular SQL IDE  ──REST──▶  Lakehold.Api
                                 │
                 ┌───────────────┴───────────────┐
                 ▼                               ▼
         CONTROL PLANE                     DATA PLANE
    ControlPlaneContext                  LakeContext
    EF model · migrations                model-less · dynamic SQL
    tenants · catalogs · history         Duckling sessions · user SQL
                 └──── DuckDB.EFCoreProvider ────┘
```

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

No DuckLake extension, no Lakehold, no metadata catalog — just Parquet. See
[`docs/EXIT-PATH.md`](docs/EXIT-PATH.md).

One caveat worth knowing: **DuckLake inlines small commits into the metadata catalog rather than
writing Parquet immediately.** A two-row insert produces no data files. Run the **Flush**
maintenance operation (or `ducklake_flush_inlined_data`) to force them out. Lakehold surfaces this
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

Verify a bundle with no Lakehold and no .NET in the loop:

```bash
duckdb -c "SELECT count(*) FROM read_parquet('…/data/main/events.parquet')"
sha256sum …/data/main/events.parquet     # compare against MANIFEST.json
```

Because it only reads, an eject never mutates the catalog and works on a read-only share.

---

## Change data capture, without a pipeline

DuckLake already records what each snapshot changed, so Lakehold exposes it directly rather than
asking you to run Debezium and Kafka to get it back out. Two surfaces, same source.

**Pull** — typed change pages:

```bash
curl "localhost:5200/api/tenants/demo/catalogs/analytics/changes?table=events&fromSnapshot=5"
```

```jsonc
{
  "fromSnapshot": 5, "toSnapshot": 9, "truncated": false,
  "changes": [
    { "snapshotId": 6, "rowId": 0, "changeType": "insert",           "row": { "id": 1, "status": "new" } },
    { "snapshotId": 8, "rowId": 3, "changeType": "update_preimage",  "row": { "id": 4, "status": "new" } },
    { "snapshotId": 8, "rowId": 3, "changeType": "update_postimage", "row": { "id": 4, "status": "shipped" } }
  ]
}
```

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
      "DeliveryTimeout": "00:00:30",
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
- **Delivery is at-least-once with a resumable cursor.** Windows advance one snapshot at a time and
  the cursor moves only after a 2xx, so a failing consumer replays rather than skips. Make your
  handler idempotent on `(snapshotId, rowId, changeType)`.
- **Payloads are HMAC-SHA256 signed** over a timestamped base — `X-Lakehold-Signature`,
  `X-Lakehold-Timestamp`, `X-Lakehold-Delivery`. Verify both the signature and the timestamp's
  freshness to reject replays. The secret is stored to sign with and is never returned by the API or
  written to a log.
- **Every node dispatches.** Duplicate notification is possible in a cluster; the receiver has to
  tolerate it anyway. Set `Enabled: false` on all but one node if that is unacceptable.

---

## Authentication

The credential names the tenant; the URL segment is validated against it rather than trusted. A token
belongs to one tenant, may be narrowed to a single catalog, and carries a role — `owner`, `editor`,
or `reader`.

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
workspace and a catalog, and it mints the token that can query them and shows it once. The same three
steps by hand:

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
  platform injects credentials.
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
- **OIDC** covers humans — configure an authority and a tenant claim. Leave it unset and the path
  stays off entirely, so an air-gapped install takes no identity-provider dependency.

The full design, including what is deliberately still open, is in
[`docs/AUTHENTICATION.md`](docs/AUTHENTICATION.md).

---

## The PostgreSQL wire endpoint

Lakehold speaks the PostgreSQL wire protocol, so a client that already speaks Postgres connects to a
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

| Postgres | Lakehold |
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
Lakehold exports it to Parquet on a schedule and can rebuild a catalog from that export.

```jsonc
{
  "Lakehouse": {
    // A sibling of the data root, never a child: DuckLake's orphan cleanup sweeps everything under
    // the data path that the catalog does not reference, and a backup is by definition unreferenced.
    "BackupRoot": "./.lakehold/backups",
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
- **Object-store backup roots cannot be pruned.** DuckDB has no delete for object stores, so set a
  lifecycle rule on the prefix. The backup says so rather than reporting "0 pruned".
- **Multi-node deployments take a lease per job per catalog**, so every node firing the same cron
  does not run the same sweep. It engages only for PostgreSQL-backed catalogs — a local file cannot
  be opened by two nodes anyway.

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

Working today: SQL IDE with catalog explorer and result grid, query history and audit, snapshot
listing for time travel, maintenance operations (flush, compact, expire, cleanup — destructive ones
dry-run by default, with explicit confirmation), scheduled maintenance with multi-node leasing,
catalog backup and restore for both local-file and PostgreSQL metadata, **verified and signed eject
bundles**, **CDC via a typed pull API and signed outbound webhooks**, a PostgreSQL wire endpoint for
`psql`, DBeaver, and Npgsql (Power BI still needs the type-catalogue shim), read-only cross-catalog
attach, tenant-scoped credentials and audit, and demo seeding.

Also shipped: **authentication and tenant identity** — API tokens with tenant and catalog scoping,
instance-scoped provisioning and bootstrap, read-only capability enforced by attachment, per-statement
audit, the PostgreSQL wire endpoint on the same token store (so revocation closes both surfaces),
OIDC, owner/editor/reader roles, and an authenticated **MCP server for AI agents** with read tools,
resources, OAuth protected-resource metadata, and operator-gated writes. See
[`docs/AUTHENTICATION.md`](docs/AUTHENTICATION.md) and [`docs/MCP.md`](docs/MCP.md); note that HTTP
API enforcement is opt-in per deployment via `Lakehold:Auth:RequireAuthentication`, while MCP always
requires a credential.

Next: an Iceberg REST Catalog endpoint so Spark, Trino, and Snowflake read Lakehold tables live with
no export, a `Lakehold.Client` package whose typed change stream turns the CDC feed into
`ChangeEvent<T>` in your own model, and read-only share links.

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
