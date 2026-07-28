# Incident-response runbook

Use this runbook for an unplanned degradation, outage, suspected data integrity problem, or security
event. For a planned restore or a loss of durable state, also use
[Disaster recovery](DISASTER-RECOVERY.md). For alert definitions and signal interpretation, use
[Monitoring and alerting](MONITORING-AND-ALERTING.md).

## Severity and response targets

The targets below are an initial operating policy. Replace them with approved service objectives;
do not silently weaken them when an incident occurs.

| Severity | Examples | Acknowledge | Update cadence |
|---|---|---:|---:|
| SEV-1 | All production queries unavailable; confirmed cross-tenant or public data exposure; durable data actively being corrupted or deleted | 5 minutes | 15 minutes |
| SEV-2 | One tenant or critical catalog unavailable; severe latency; missed recovery-point objective; repeated backup failure; suspected credential compromise | 15 minutes | 30 minutes |
| SEV-3 | Degraded non-critical feature; isolated CDC failure; capacity or backup warning with recovery margin remaining | 4 business hours | Daily or on material change |
| SEV-4 | Documentation, cosmetic, or low-risk operational defect with no current user impact | Next planning cycle | As agreed |

Raise severity whenever impact, scope, or integrity is uncertain. Lower it only after evidence
confirms the smaller scope.

## Roles

For SEV-1 and SEV-2, name these roles in the incident record:

- **Incident commander:** owns severity, priorities, decisions, delegation, and closure.
- **Operations lead:** performs or coordinates technical actions.
- **Scribe:** records UTC timestamps, observations, commands, outputs, and decisions.
- **Communications lead:** sends user and stakeholder updates.
- **Security lead:** required for exposure, token, secret, or authorization incidents.

One person may hold several roles on a small team, but the incident commander should avoid becoming
the only person typing commands and the only person deciding whether they were safe.

## First 10 minutes

1. Open an incident record and assign an incident identifier, severity, commander, and UTC start
   time.
2. State the observed impact without guessing at the cause: affected users, tenants, catalogs,
   surfaces, and first known failure.
3. Freeze deployments, cleanup, expiry, schema changes, token rotation, and infrastructure
   automation that could erase evidence or widen impact.
4. If data integrity or unauthorized access is possible, stop writes before optimizing availability.
5. Capture the minimal safe evidence below.
6. Choose the matching runbook branch and declare the next update time.

Authenticated API examples expect `LAKEHOLD_URL`, `TENANT`, `CATALOG`, and `LAKEHOLD_TOKEN` as
defined in the [operations hub](../OPERATIONS.md#routine-operations). Do not put token plaintext in
shell history or the incident record.

Run from the deployment directory:

```bash
date -u
docker compose -f compose.production.yaml ps
docker compose -f compose.production.yaml images
curl --fail --silent --show-error \
  "http://127.0.0.1:${LAKEHOLD_PORT:-8080}/health"
docker inspect \
  --format '{{.Name}} status={{.State.Status}} health={{if .State.Health}}{{.State.Health.Status}}{{end}} oom={{.State.OOMKilled}} restarts={{.RestartCount}}' \
  lakehold-api-1 lakehold-workbench-1
```

Container names can differ; take the exact names from `docker compose ps`. Capture a bounded log
window:

```bash
docker compose -f compose.production.yaml logs \
  --since 30m --timestamps api workbench
```

Review before sharing it. The first API start can log a one-time bootstrap credential. Do not collect
an environment dump, `docker inspect`'s full JSON, or a rendered Compose configuration into an
incident channel; they can contain secrets.

## Rapid classification

| Observation | Likely failure domain | Next action |
|---|---|---|
| Host `/health` cannot connect and both containers are absent | Host, Docker, or deployment | Check host/runtime, then [service unavailable](#service-unavailable) |
| `workbench` is up but `api` is unhealthy | API startup or control plane | Probe API internally and inspect API logs |
| `/alive` is healthy but `/health` is unhealthy | Control-plane connectivity or file access | [Control plane unavailable](#control-plane-unavailable) |
| `/health` is healthy but `/workbench` fails | nginx, UI container, proxy, or host port | [Workbench or proxy failure](#workbench-or-proxy-failure) |
| Health is good; requests return 401/403/404 | Token validity, role, tenant, or catalog scope | [Authentication or authorization failure](#authentication-or-authorization-failure) |
| Health is good; queries queue or time out | Tenant session contention, cold starts, memory, or storage | [Query degradation](#query-degradation) |
| Schedule entries fail or stop appearing | Scheduler, storage, permissions, or restart | [Maintenance or backup failure](#maintenance-or-backup-failure) |
| CDC cursor stops or webhook failures rise | Receiver, network, signing secret, or dispatcher | [CDC delivery failure](#cdc-delivery-failure) |
| Volume near full, write errors, or OOM kill | Capacity/resource exhaustion | [Resource exhaustion](#resource-exhaustion) |
| Unexpected token use, auth failures, or data visibility | Security | [Security or isolation incident](#security-or-isolation-incident) |

## Service unavailable

### Confirm

```bash
docker compose -f compose.production.yaml ps
docker compose -f compose.production.yaml logs \
  --since 15m --timestamps api workbench
docker system df
```

Check the host's memory, filesystem, volume mount, certificate or reverse proxy, and container
runtime with the platform's normal tools.

### Contain

- Stop deployment automation and repeated restart loops while the failure is unknown.
- If the host is OOM-killing the API, stop new traffic before restarting it.
- If writes may be partially completing, announce a write freeze and retain the state volume.

### Recover

- If a container exited because of a transient host failure and state is intact, start the pinned
  deployment with `docker compose -f compose.production.yaml up -d --wait`.
- If a new image caused the outage and persisted state is compatible, deploy the last-known-good
  pinned tag.
- If the volume is missing, corrupt, or mounted empty, stop. Use the disaster-recovery runbook; do
  not let a fresh empty volume accept writes under the production name.

### Verify

Require all of: Compose health is healthy, `/health` returns `Healthy`, authenticated tenant listing
works, a read-only canary query returns its expected result, scheduled maintenance state is
understood, and error/latency signals return to baseline.

## Control plane unavailable

`/alive` answers whether the process can serve a request. `/health` additionally calls
`CanConnectAsync` on the configured PostgreSQL control plane. A healthy `/alive` with an unhealthy
`/health` means restarts are unlikely to repair database connectivity, credentials, or PostgreSQL.

Probe from inside the API container because nginx does not publish `/alive`:

```bash
docker compose -f compose.production.yaml exec api \
  curl --fail --silent --show-error http://127.0.0.1:5200/alive
docker compose -f compose.production.yaml exec api \
  curl --fail --silent --show-error http://127.0.0.1:5200/health
```

Check PostgreSQL reachability, TLS, credential expiry/rotation, connection limits, replicas/failover,
and the database provider's storage/health signals.

- Missing or corrupt control plane: restore PostgreSQL through its approved PITR/dump process.
  Catalog backup generations do not contain tenants, catalog descriptors, token hashes,
  subscriptions, or audit records.
- Credential or network regression: correct it through the infrastructure change process, then
  restart only the API if connection pools do not recover.
- Database disk full: stop writes and follow the PostgreSQL provider's resource-exhaustion procedure.
  Deleting control-plane tables or DuckLake metadata schemas is forbidden.

## Workbench or proxy failure

If `/health` works through the host but `/workbench` does not:

```bash
curl --fail --silent --show-error \
  "http://127.0.0.1:${LAKEHOLD_PORT:-8080}/workbench"
docker compose -f compose.production.yaml exec workbench \
  wget -qO- http://127.0.0.1:8080/workbench
docker compose -f compose.production.yaml logs \
  --since 15m --timestamps workbench
```

An internal success and host failure points to port publishing, host firewall, TLS termination, or an
upstream proxy. An internal failure points to the workbench image or nginx configuration. The API
can remain healthy while the UI is down; communicate the actual surface impact.

## Authentication or authorization failure

Interpret status codes before changing policy:

- `401`: token missing, malformed, expired, revoked, or unknown.
- `403`: authenticated subject lacks the required capability.
- `404`: tenant or catalog is absent or intentionally hidden from that subject.

Check a token's stored metadata through an already authorized owner or instance credential. Never
paste the failing token into an incident channel or query its hash directly from the database.

- Confirm `LAKEHOLD_REQUIRE_AUTH` was not changed.
- Confirm the token role (`owner`, `editor`, `reader`), tenant, optional catalog narrowing, expiry,
  and revocation time.
- For a suspected leak, use an unaffected owner credential to revoke the token, rotate any adjacent
  secrets, and preserve audit and ingress evidence.
- Do not set `LAKEHOLD_REQUIRE_AUTH=false` to make an incident disappear. In production that trusts
  every reachable caller as every route-named tenant.

## Query degradation

Use traces and metrics to separate three different problems:

| Signal | Interpretation | Response |
|---|---|---|
| High `lakehold.session.queue.duration` | Work is waiting behind another statement on one catalog | Identify the tenant/catalog from traces; reduce concurrency or isolate workload |
| High `lakehold.session.start.duration` and pool misses | Cold attach or session churn | Check overflow evictions, `MaxWarmSessions`, storage latency, and memory headroom |
| High `lakehold.query.duration` with low queue time | DuckDB execution or storage is slow | Inspect operation outcome, host I/O/CPU, query history, and storage availability |

Cancel or stop a known runaway client at the client or ingress first. The statement timeout defaults
to two minutes for HTTP materializing queries, but wire queries stream and have their own connection
limits. Do not kill the API process solely to cancel one query unless the node itself is at risk.

If one catalog causes system-wide resource pressure, stop its traffic or move it to an isolated node.
LakeHold serializes work per catalog; adding concurrent requests to the same catalog increases queue
time rather than compute.

## Maintenance or backup failure

Collect both the recent schedule view and exported logs:

```bash
curl --fail --silent --show-error \
  -H "Authorization: Bearer $LAKEHOLD_TOKEN" \
  "$LAKEHOLD_URL/api/maintenance/schedule"
docker compose -f compose.production.yaml logs \
  --since 2h --timestamps api
```

The schedule view disappears on restart. In logs, event 2000 is a scheduled job failure, 2001 records
the effective cron expressions, and 2002 means scheduling is disabled.

1. Confirm the scheduler is enabled and the six-field Quartz cron is valid.
2. Distinguish `skipped: another node holds the maintenance lease` from a failure.
3. Check volume space, permissions, data/metadata reachability, and object-store credentials.
4. List catalog backups and confirm the newest generation has `complete: true`.
5. After fixing the cause, run one manual non-destructive operation and verify its response:

```bash
curl --fail --silent --show-error -X POST \
  -H "Authorization: Bearer $LAKEHOLD_TOKEN" \
  "$LAKEHOLD_URL/api/tenants/$TENANT/catalogs/$CATALOG/maintenance/backup"
```

Do not restore an incomplete generation. Do not delete it during the incident; its timestamps and
contents are evidence of where the export stopped.

## CDC delivery failure

LakeHold delivery is at-least-once. The cursor advances only after a 2xx response, so a receiver can
see a window again after a timeout or a crash between delivery and cursor persistence. Consumers
must deduplicate by snapshot and row identifiers.

List the affected catalog's subscriptions and compare `lastDeliveredSnapshot`,
`consecutiveFailures`, `lastAttemptUtc`, and `lastError` with the catalog's latest snapshot:

```bash
curl --fail --silent --show-error \
  -H "Authorization: Bearer $LAKEHOLD_TOKEN" \
  "$LAKEHOLD_URL/api/tenants/$TENANT/catalogs/$CATALOG/subscriptions"
```

Check the receiver's health, TLS and certificate validity, DNS/route/firewall changes, response
status and latency, and its verification of the LakeHold signature. The dispatcher retries with
backoff up to the configured 30-minute maximum; restoring a receiver should resume from the stored
cursor without manual intervention.

Do not edit `LastDeliveredSnapshot` in the control plane. Moving it forward skips changes; moving it
back replays them. Deleting and recreating a subscription also starts the new subscription at the
catalog's current snapshot, so it is not a safe retry or secret-rotation procedure when an
undelivered window must be preserved. For a compromised webhook secret, contain the receiver and
engage the data owner before replacing the subscription; record whether an explicit replay is
required.

Verify recovery by observing a 2xx, `consecutiveFailures` reset to zero, cursor advancement to the
expected snapshot, and correct deduplication at the consumer.

## Resource exhaustion

For a full volume or filesystem:

- freeze writes;
- identify which state subtree is growing without deleting it;
- expand the volume or move state through an approved migration;
- verify backup retention and the off-host archive process;
- if object-store backup retention is in use outside the packaged API, verify its lifecycle policy.

For memory pressure or OOM:

- stop or shed the offending workload;
- compare the API container limit with `Lakehouse:MemoryLimit`, warm session count, and per-session
  concurrency;
- look for overflow eviction and pool-miss churn;
- only raise limits after confirming host headroom.

Docker's `json-file` logs are capped at three 10 MB files per container by the production Compose
file; deleting logs is not an adequate disk-capacity strategy and removes incident evidence.

## Security or isolation incident

Treat suspected unauthorized visibility, cross-tenant access, exposed credentials, or altered
backup/eject manifests as SEV-1 until disproved.

1. Stop affected ingress and writes without deleting containers, volumes, tokens, or logs.
2. Preserve host, proxy, API, identity-provider, and secret-store audit evidence with UTC times.
3. Revoke affected LakeHold tokens using an unaffected owner or instance credential.
4. Rotate exposed webhook, database, object-store, signing, and TLS secrets at their source.
5. Identify affected tenant/catalog scope from authorization and trace attributes; do not run broad
   exploratory SQL against customer data.
6. Engage the security and privacy process for notification and evidence retention.
7. Restore service only after the access path is closed and tenant isolation is independently
   verified.

Do not destroy the suspected credential record before evidence is captured. Revocation preserves
metadata and audit linkage; deletion can remove context without making an already copied token safe.

## Communication template

Use facts, UTC times, impact, and the next update time:

> **Investigating — `<incident id>` — `<severity>`**<br>
> Starting at `<UTC time>`, `<surface/tenant/catalog scope>` experienced `<observable impact>`.<br>
> We have `<containment action>` and are investigating `<confirmed failure domain, not guessed root
> cause>`. Data integrity is `<confirmed intact / under investigation / affected>`.<br>
> Next update by `<UTC time>`.

Recovery update:

> **Monitoring — `<incident id>`**<br>
> Service was restored at `<UTC time>` by `<action>`. We verified `<health, authenticated canary,
> row-count/integrity, maintenance evidence>`. We are monitoring `<signals>` and will update by
> `<UTC time>`.

Never include tokens, SQL, result data, internal paths, connection strings, or unreviewed log excerpts.

## Closure and post-incident review

Close only when:

- impact has ended and a defined monitoring period passed;
- data integrity and tenant scope are verified, not assumed from health;
- alerts, backups, schedules, and CDC are operating;
- temporary access, policy, or capacity changes are removed or tracked;
- the incident timeline and user communication are complete;
- every follow-up has an owner and due date.

For SEV-1 and SEV-2, complete a blameless review within five business days: impact, detection,
timeline, contributing conditions, why safeguards did or did not work, recovery evidence, and
specific prevention/detection/mitigation actions. Update this runbook when responders had to invent
a step under pressure.
