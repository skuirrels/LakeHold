# Operating LakeHold

This is the entry point for running LakeHold after installation. It covers the operating model,
required controls, routine work, and the runbooks to use when something fails.

- [Incident response](runbooks/INCIDENT-RESPONSE.md) — severity, roles, first response, diagnosis,
  containment, recovery, communication, and post-incident review.
- [Disaster recovery](runbooks/DISASTER-RECOVERY.md) — what each backup protects, recovery
  objectives, full-node and catalog recovery, validation, and restore drills.
- [Monitoring and alerting](runbooks/MONITORING-AND-ALERTING.md) — health endpoints, OpenTelemetry
  signals, dashboards, initial alert thresholds, and alert testing.
- [Exit path](EXIT-PATH.md) — format-level recovery and migration details. This is supporting
  technical material, not a substitute for the disaster-recovery runbook.

## Supported operating profile

PostgreSQL is the required shared control plane and the default DuckLake metadata database. API and
worker nodes may scale horizontally, but each query runs inside one node's DuckDB process; LakeHold
does not claim distributed SQL. S3, GCS, or Azure Parquet storage is recommended for multi-node
deployments. Local Parquet is supported only as a single-node or externally shared-filesystem
profile. Remaining adversarial multi-tenant artifact/background-job gates are owned by the
[production-readiness roadmap](PRODUCTION-READINESS-ROADMAP.md).

`compose.production.yaml` runs two required containers and one optional query planner:

| Component | Responsibility | Durable state | Health |
|---|---|---|---|
| `workbench` | nginx, private Angular workbench, same-origin `/api` proxy | None | `/workbench` inside the container |
| `api` | PostgreSQL-backed control plane, DuckLake sessions, schedules, CDC, optional wire and MCP endpoints | PostgreSQL plus configured Parquet storage | `/health` readiness and `/alive` liveness |
| `linq-compiler` (profile `linq`) | Isolated, catalog-credential-free C# LINQ-to-SQL planning | None | `/health` liveness and `/ready` provider translation |

SQL does not depend on the compiler. Enable C# LINQ with a secret from the deployment's secret store:

```bash
export LAKEHOLD_LINQ_PLANNER_KEY='<long-random-secret>'
docker compose -f compose.production.yaml --profile linq up -d
```

Omitting the profile removes the component and the API omits C# LINQ from language discovery. The
API and compiler must receive the same secret; a mismatch does not hide the language, it offers it
as unavailable in the Workbench with that as the stated reason and logs a warning naming the
planner. Discovery is per request and uncached, so a compiler that recovers becomes usable on the
next page load without restarting the API. The compiler is internal-only, read-only, non-root,
has no state/catalog volume, and is bounded by the Compose CPU, memory, and process limits. See
[C# LINQ in the Workbench](LINQ_WORKBENCH.md) for its request and security contract.

The public host port is `8080` by default. nginx proxies `/health` and `/api`; it does not expose
`/alive`. Probe `/alive` only from the container or private service network.

The evaluation-only `make demo` path switches a clean deployment checkout to `main`, fast-forwards
it from `origin/main`, and then enables and builds the LINQ profile by default. It generates a
high-entropy planner credential in memory and supplies the same value to the API and compiler, so
the demo operator does not configure or retain a key. Standard and remote production deployments
continue to take their planner credential from the deployment secret store.

The published API image includes the signed DuckDB extension binaries required by LakeHold plus the
Excel extension used for `.xlsx` imports. They are pinned to the exact DuckDB engine version and to
the image architecture, and every compute session loads them from the non-root `app` user's local
cache. A fresh production container therefore does not contact `extensions.duckdb.org` when its
first catalog session starts. From-source host development uses DuckDB's normal user cache; the
development Compose API image bakes in the same platform-specific cache before `dotnet watch` starts.

The default state volume contains:

| Path | Contents | Rebuilt from source? |
|---|---|---|
| `data/` | local Parquet data and delete files | No |
| `backups/` | catalog-metadata backup generations | No; and on the same volume by default |
| `ejects/` | verified reader-independent export bundles | No |

The PostgreSQL control plane and DuckLake metadata are outside this volume and require native
backup/PITR. Everything inside the volume is state when local Parquet or local artifacts are used.
Never use `docker compose down -v` in an operating or recovery procedure.

## Assign ownership before production

Replace the placeholders below in the deployment's private operations repository or on-call system.
Do not put personal telephone numbers, bearer tokens, or secret-store paths in this public document.

| Responsibility | Required assignment |
|---|---|
| Service owner | Team accountable for availability, risk acceptance, and post-incident actions |
| Primary on-call | First responder for pages |
| Incident commander | Coordinates a severity 1 or 2 incident; does not have to perform commands |
| Infrastructure owner | Host, container runtime, network, volume, and object-store recovery |
| Security contact | Credential compromise, unexpected authorization failures, or data exposure |
| Communications owner | User and stakeholder updates |
| Escalation channel | Private channel or incident-management system |

Record the current names, rota, vendor contacts, and access procedure beside the deployment. Test
that the on-call engineer can reach the host, telemetry backend, secret store, off-host backup, and
image registry without relying on the failed service.

## Production entry gate

Do not treat a healthy container as proof that the service is operable. Before accepting production
traffic, verify all of the following:

- A release is pinned with `LAKEHOLD_TAG`; production does not track `latest`.
- Both required PostgreSQL connection strings are supplied through the secret store, use TLS where
  traffic leaves a trusted network, and have a tested backup/PITR policy.
- An owner token plus an instance provisioning token are held in an approved secret store.
  Authentication is unconditional, so there is no enforcement switch to verify — but there is one to
  check the other way: confirm `Lakehold:Auth:DemoTenant` and `DemoCatalog` are **empty** unless
  this node is deliberately publishing a catalog to anonymous readers. LakeHold stores only token
  hashes and cannot recover plaintext tokens.
- The state volume has a consistent off-host backup and checksum. A backup that remains on the same
  host or volume does not protect against loss of that host or volume.
- Any external object store or PostgreSQL metadata service has its own versioning, backup, and
  recovery procedure. A LakeHold catalog backup is not a copy of table data.
- `OTEL_EXPORTER_OTLP_ENDPOINT` sends logs, metrics, and traces to a retained backend, and the
  dashboards and alerts in the monitoring runbook exist.
- `/health` is monitored through the same private ingress path clients use. `/health` and `/alive`
  are not exposed by a public ingress. The optional PostgreSQL-wire listener is not currently part
  of HTTP readiness; when enabled, probe its TCP port and an authenticated query separately.
- The incident contacts and communications channel are assigned.
- A restore drill has met the deployment's approved recovery-point and recovery-time objectives.
- Host capacity leaves headroom above the API container limit; disk or volume alerts fire before
  the state path is full.
- If the optional C# LINQ surface is enabled, its shared secret is non-default, `/ready` proves a
  provider translation, it is reachable only from the API network, and removing the profile has
  been verified to leave SQL execution healthy.
- This deployment is limited to one tenant or mutually trusted SQL users. Do not accept mutually
  untrusted tenants on a shared query node until Phase 3 arbitrary-SQL containment in the
  [production-readiness roadmap](PRODUCTION-READINESS-ROADMAP.md) is complete.

If any item is intentionally absent, record the owner, compensating control, expiry date, and risk
acceptance. An undocumented exception is an operational gap.

## Routine operations

Run commands from the directory containing `compose.production.yaml`. From a repository checkout,
the Make targets are the supported shortcuts:

```bash
make status
make logs
make deploy
make stop
make backup-state
```

The equivalent base command on an install that downloaded only the Compose file is:

```bash
docker compose -f compose.production.yaml
```

Authenticated examples use these shell variables. Read the token without placing it in shell
history, run from a trusted operator host, and unset it when finished:

```bash
LAKEHOLD_URL="http://127.0.0.1:${LAKEHOLD_PORT:-8080}"
TENANT="<tenant-slug>"
CATALOG="<catalog-name>"
read -r -s LAKEHOLD_TOKEN
```

Use the following minimum cadence, adjusted from evidence after the first month:

| Cadence | Checks |
|---|---|
| Every minute | Readiness, request errors/latency, container restarts, and host saturation |
| Every 15 minutes | A successful flush occurred within twice the configured interval |
| Hourly | A complete catalog backup exists for every writable catalog |
| Daily | State-volume capacity, failed maintenance, CDC delivery failures, expiring certificates and tokens |
| Weekly | Off-host backup completion, checksum, retention, image/security advisories, and capacity trend |
| Monthly | Restore a representative backup into an isolated environment and validate it |
| Quarterly | Run a total-node-loss exercise, test paging and contacts, and review RPO/RTO evidence |

The schedule API is useful for immediate diagnosis:

```bash
curl --fail --silent --show-error \
  -H "Authorization: Bearer $LAKEHOLD_TOKEN" \
  "$LAKEHOLD_URL/api/maintenance/schedule"
```

It is only an in-memory ring of the latest 100 runs and is empty after an API restart. Exported
telemetry and off-host backup inventory are the durable evidence that maintenance actually ran.

For a LINQ-enabled deployment, `GET /api/query-languages` is the user-facing readiness check. SQL
must always be present; `csharp-linq` is present only while the optional planner's descriptor check
succeeds. If it disappears, check the planner container, the shared-secret match, `/ready`, compiler
timeout/queue saturation, and the internal network. Do not expose the planner port publicly or move
catalog credentials into it. Planner failure is not a reason to restart or disable the SQL path.

For catalogs with CDC consumers, also compare source snapshots, subscription cursors, and registered
replica checkpoints:

```bash
curl --fail --silent --show-error \
  -H "Authorization: Bearer $LAKEHOLD_TOKEN" \
  "$LAKEHOLD_URL/api/tenants/$TENANT/catalogs/$CATALOG/subscriptions"
curl --fail --silent --show-error \
  -H "Authorization: Bearer $LAKEHOLD_TOKEN" \
  "$LAKEHOLD_URL/api/tenants/$TENANT/catalogs/$CATALOG/cdc/consumers"
```

A lagging subscription remains a retention owner even while paused; an active lagging pull consumer
does too. Snapshot expiry reports either during dry-run and refuses apply. Advance a consumer only
after its target transaction commits. Delete it only when the mirror is intentionally abandoned or
will be re-bootstrapped.

## Deploy and roll back

Capture the current image digests and take a consistent off-host state backup before an upgrade.
Then deploy a pinned release:

```bash
LAKEHOLD_TAG=2.0.1 make deploy
```

Released images are published under both `2.0.1` and `v2.0.1`, so either form of a release's
version pins the same build. `latest` moves on every release and is not a pin.

`make deploy` pulls the images and waits for both container health checks. A failed health check is a
failed deployment even if the containers are running.

For an application regression, redeploy the last-known-good tag:

```bash
LAKEHOLD_TAG=v2.0.0 make deploy
```

`v2.0.0` stands in for the last release known good in *this* deployment, which is whatever the
deployment record names — not necessarily the immediate predecessor.

This rolls back images, not state. The production PostgreSQL control plane applies ordered EF Core
migrations under an advisory lock; LakeHold does not automatically run down-migrations when an
older image is redeployed. Do not assume an older image can safely read state first opened by a
newer image. If the change touched persisted state, use the disaster-recovery runbook and the
pre-upgrade backup in an isolated environment before changing production. The additive schema
adapter is only a compatibility bridge for importing legacy DuckDB control-plane files.

## Changes requiring an operator review

Require a second operator, recorded change window, fresh backup, and rollback decision for:

- disabling authentication or publishing the API, MCP, or PostgreSQL-wire port;
- changing state, metadata, data, backup, or eject storage;
- moving from local metadata to PostgreSQL metadata;
- changing maintenance cadence, retention, or object-store lifecycle rules;
- applying `expire` or `cleanup`;
- rotating signing keys, webhook secrets, database credentials, or API tokens;
- increasing DuckDB memory, threads, warm sessions, connection limits, or container memory;
- restoring data, replacing a metadata file, or changing a catalog descriptor.

`expire` and `cleanup` are destructive and dry-run by default. Save and review their dry-run output
before adding `?apply=true`; never apply either while diagnosing an unrelated incident.

## Operational evidence and sensitive data

For each deployment, retain:

- image tag and digest, deployment time, configuration change reference, and operator;
- health and alert history;
- exported logs, metrics, and traces for the agreed retention window;
- backup inventory, checksums, off-host copy status, and restore-drill results;
- incident timelines, decisions, user impact, and follow-up owners.

Do not attach raw environment dumps or unreviewed start-up logs to tickets. Start-up logs can contain
the one-time bootstrap token. Redact bearer tokens, connection strings, secret names, signed webhook
headers, storage credentials, and user SQL or result data. LakeHold telemetry intentionally omits
submitted SQL; preserve that boundary in external dashboards and log processors.
