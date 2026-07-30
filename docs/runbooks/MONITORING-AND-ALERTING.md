# Monitoring and alerting

This guide defines the minimum production signals, dashboards, alerts, and response links for
LakeHold. It starts with conservative thresholds. Tune them from measured traffic, but retain alerts
for availability, data-protection jobs, capacity, and security even on a quiet deployment.

## Signal sources

| Source | What it proves | Limit |
|---|---|---|
| `GET /health` | API is serving and can connect to the control plane | Does not attach or query every catalog |
| `GET /alive` | API process can serve a request | Does not test dependencies; not proxied by production nginx |
| Docker health/restart/OOM state | Container lifecycle | Does not prove application integrity |
| OpenTelemetry metrics | Request, runtime, query, session, wire, cache, and maintenance behavior | Requires an external OTLP collector/backend |
| OpenTelemetry traces | Per-request tenant/catalog attribution and operation timing | Sampling must retain errors and slow traces |
| Structured application logs | Startup, scheduler, and exception detail | Local Docker logs rotate and are not durable |
| `/api/maintenance/schedule` | Latest 100 per-catalog scheduled runs | In-memory; cleared by restart |
| Backup/eject manifests | Artifact completion and recovery point | Same-volume artifacts are not off-host DR |
| Host/storage metrics | CPU, memory, filesystem, volume, and I/O saturation | Supplied by the deployment platform |

Set `OTEL_EXPORTER_OTLP_ENDPOINT` to a collector reachable from the API. The production Compose file
sets `OTEL_SERVICE_NAME=lakehold-api`; an unset endpoint exports nothing. Compose includes no
collector or alert manager.

LakeHold emits logs, metrics, and traces through OpenTelemetry. EF instrumentation strips
`db.statement` and `db.query.text`, and LakeHold spans never record submitted SQL, credentials,
metadata sources, or data paths. Do not re-add those fields in collectors or processors.

## Health probes

Externally through the private workbench ingress:

```bash
curl --fail --silent --show-error \
  "http://127.0.0.1:${LAKEHOLD_PORT:-8080}/health"
```

Internally:

```bash
docker compose -f compose.production.yaml exec api \
  curl --fail --silent --show-error http://127.0.0.1:5200/health
docker compose -f compose.production.yaml exec api \
  curl --fail --silent --show-error http://127.0.0.1:5200/alive
```

Both return only `Healthy` or `Unhealthy` and disable caching. Keep them unauthenticated for probes,
but block both at public ingress.

- **Readiness `/health`:** includes `control-plane`. Remove the node from traffic when it fails.
- **Liveness `/alive`:** includes only the in-process `self` check. Restart the process when it
  fails. Do not add external dependencies; that would turn dependency failure into a restart loop.

Use a 10–15 second interval, 5 second timeout, and at least three consecutive failures before paging.
The API container itself uses 15 seconds, 5 seconds, a 45-second start period, and five retries.

## LakeHold metrics

The canonical OpenTelemetry instrument names are below. A Prometheus exporter may normalize dots to
underscores and append unit or counter suffixes; verify the actual backend names after deployment.

| Instrument | Type/unit | Use |
|---|---|---|
| `lakehold.query.duration` | histogram, seconds | End-to-end statement latency by `lakehold.outcome` |
| `lakehold.query.rows` | histogram, rows | Returned result size |
| `lakehold.query.truncated` | counter | Results hitting `MaxRowsPerResult` |
| `lakehold.session.queue.duration` | histogram, seconds | Time waiting for a catalog's serialized session gate |
| `lakehold.session.start.duration` | histogram, seconds | Cold extension load and catalog attach |
| `lakehold.sessions.warm` | up/down counter | Current warm compute sessions |
| `lakehold.session.evictions` | counter | Evictions by `lakehold.reason`: `idle`, `overflow`, `explicit` |
| `lakehold.pool.requests` | counter | Session pool hit/miss |
| `lakehold.pgwire.connections` | up/down counter | Current wire connections |
| `lakehold.pgwire.connections.closed` | counter | Clean, refused, or faulted closes |
| `lakehold.pgwire.auth.failures` | counter | Rejected wire authentication attempts |
| `lakehold.catalog.cache.requests` | counter | Catalog-cache hit/miss |
| `lakehold.maintenance.duration` | histogram, seconds | Maintenance latency by operation, dry run, and outcome |
| `lakehold.maintenance.lease.attempts` | counter | Scheduled lease `acquired` or `held_elsewhere` |

Metric tags are deliberately bounded. Tenant and catalog are span attributes
(`lakehold.tenant`, `lakehold.catalog`), not metric dimensions, to prevent per-tenant time-series
explosion. Find an affected tenant through sampled error/slow traces after a metric identifies the
class of failure.

## Required dashboards

### Availability and requests

- ingress and API request rate, status class, p50/p95/p99 duration;
- `/health` and `/alive` status by node;
- container status, restart count, OOM kills, and deployed image digest;
- control-plane connectivity failures;
- current incident and last deployment annotation.

### Query and session behavior

- `lakehold.query.duration` by outcome;
- query error and truncation rate;
- p95 `lakehold.session.queue.duration` beside p95 query duration;
- p95 `lakehold.session.start.duration`;
- warm sessions against `Lakehouse:MaxWarmSessions`;
- pool miss rate and evictions by reason;
- host CPU, API/container memory, filesystem I/O, and object-store latency.

### Data protection and maintenance

- last successful flush and backup per catalog;
- failed scheduled runs and incomplete backup generations;
- backup recovery point, age, size, manifest completeness, off-host copy, checksum, and retention;
- maintenance duration and lease outcomes by operation;
- state-volume use and forecasted exhaustion;
- last successful catalog and full-node restore drill.

The schedule endpoint alone cannot power this dashboard because it is in-memory and bounded. Export
event 2000 failures and maintenance metrics, and inventory manifests from outside the API.

### Security and integration

- HTTP 401/403 rate and unusual changes from baseline;
- wire authentication failures and refused connections;
- token creation, revocation, expiry, and break-glass use from the approved audit source;
- CDC subscriptions with consecutive failures, last attempt/error, and cursor lag;
- TLS/certificate and external credential expiry;
- public exposure checks for API, MCP, PostgreSQL wire, `/health`, and `/alive`.

## Initial alert policy

Every page must include service, environment, severity, first observed time, current value, threshold,
dashboard link, and a link to the exact incident-response section. Deduplicate by failure domain so a
control-plane outage does not page separately for every failed API route.

| Alert | Initial condition | Severity | Runbook |
|---|---|---|---|
| Service not ready | `/health` fails for 3 consecutive probes or all nodes are unready | SEV-1 if all traffic is unavailable; otherwise SEV-2 | [Service/control plane unavailable](INCIDENT-RESPONSE.md#control-plane-unavailable) |
| Process not alive | `/alive` fails for 3 probes or restart loop begins | SEV-2, SEV-1 if all nodes | [Service unavailable](INCIDENT-RESPONSE.md#service-unavailable) |
| Elevated server errors | 5xx > 5% for 5 minutes with at least 20 requests | SEV-2 | [Rapid classification](INCIDENT-RESPONSE.md#rapid-classification) |
| Query errors | Error outcome materially above baseline for 10 minutes | SEV-2/3 by impact | [Query degradation](INCIDENT-RESPONSE.md#query-degradation) |
| Query queue saturation | p95 queue > 5 seconds for 10 minutes, or queue time > 50% of p95 query time | SEV-3; SEV-2 if critical workload breaches SLO | [Query degradation](INCIDENT-RESPONSE.md#query-degradation) |
| Session churn | Pool miss rate > 20% and overflow evictions rise for 15 minutes | SEV-3 | [Query/resource degradation](INCIDENT-RESPONSE.md#query-degradation) |
| OOM kill | Any API OOM kill | SEV-2; SEV-1 if repeated outage | [Resource exhaustion](INCIDENT-RESPONSE.md#resource-exhaustion) |
| State volume warning | > 80% used or forecast < 7 days | SEV-3 | [Resource exhaustion](INCIDENT-RESPONSE.md#resource-exhaustion) |
| State volume critical | > 90% used or forecast < 24 hours | SEV-2 | [Resource exhaustion](INCIDENT-RESPONSE.md#resource-exhaustion) |
| Flush overdue | No successful flush for 2 configured intervals on a writable catalog | SEV-2 | [Maintenance/backup failure](INCIDENT-RESPONSE.md#maintenance-or-backup-failure) |
| Backup failed | 2 consecutive backup failures for a catalog | SEV-2 | [Maintenance/backup failure](INCIDENT-RESPONSE.md#maintenance-or-backup-failure) |
| Backup overdue | No complete backup for 2 configured intervals | SEV-2 | [Maintenance/backup failure](INCIDENT-RESPONSE.md#maintenance-or-backup-failure) |
| Off-host backup overdue | No verified off-host state copy inside the approved RPO | SEV-2 | [Disaster recovery](DISASTER-RECOVERY.md) |
| Incomplete generation | A new generation remains without a manifest past the normal job duration | SEV-3, SEV-2 if no newer complete backup | [Maintenance/backup failure](INCIDENT-RESPONSE.md#maintenance-or-backup-failure) |
| Maintenance stuck | Duration exceeds 2× its 30-day p95 or the lease duration | SEV-3 | [Maintenance/backup failure](INCIDENT-RESPONSE.md#maintenance-or-backup-failure) |
| Wire connection saturation | Connections > 80% of `MaxConnections` for 10 minutes | SEV-3 | [Query/resource degradation](INCIDENT-RESPONSE.md#query-degradation) |
| Authentication anomaly | Wire auth failures or HTTP 401s exceed baseline by an agreed rate | SEV-2/3; SEV-1 with exposure evidence | [Security incident](INCIDENT-RESPONSE.md#security-or-isolation-incident) |
| CDC delivery failing | Consecutive failures ≥ 3 or cursor lag exceeds the consumer SLO | SEV-3; SEV-2 for critical integrations | [CDC delivery failure](INCIDENT-RESPONSE.md#cdc-delivery-failure) |
| CDC lease takeover | `lakehold.cdc.lease.takeovers` rises outside a planned node restart | SEV-3; investigate worker health and PostgreSQL latency | [CDC delivery failure](INCIDENT-RESPONSE.md#cdc-delivery-failure) |
| CDC worker saturation | `lakehold.cdc.workers.active` stays at `MaxConcurrentSubscriptions` and `lakehold.cdc.snapshot.lag` rises | SEV-3 | [CDC delivery failure](INCIDENT-RESPONSE.md#cdc-delivery-failure) |
| Restore drill missed/failed | Required monthly or quarterly drill absent or failed | SEV-3 governance alert | [Disaster recovery](DISASTER-RECOVERY.md#restore-drills) |

For a low-traffic deployment, ratio alerts alone are noisy: one failed request can be 100%. Always
combine a ratio with a minimum count, or use an absolute failure count and longer window.

The five-second queue and 20% pool-miss values are starting points, not product limits. Replace them
after establishing a 30-day baseline and service objectives. Do not tune away backup, volume,
readiness, OOM, or security alerts merely because they fired correctly.

## Logs and events

The production containers use Docker's `json-file` driver with three 10 MB files each. This protects
the host from unbounded logs but is not sufficient retention. Export logs before rotation.

Important scheduler events:

| Event ID | Level | Meaning |
|---:|---|---|
| 2000 | Error | Scheduled operation failed for a tenant/catalog |
| 2001 | Information | Effective flush, backup, and compact schedules at startup |
| 2002 | Information | Scheduling is disabled |

Alert on event 2000. Inventory 2001 at deployment so an unexpected cadence is detected. Treat 2002
as an alert in production unless a recorded change explicitly disabled schedules.

Log searches and exported fields must redact:

- bootstrap and bearer tokens;
- PostgreSQL and object-store credentials or connection strings;
- webhook secrets and signed headers;
- eject signing keys;
- user SQL and result data.

## Backup monitoring

A successful HTTP response or scheduler log is not enough. For every writable catalog, verify:

1. a generation exists within the expected interval;
2. `complete` is true, which means `manifest.json` exists and parsed;
3. the manifest timestamp and snapshot match the intended recovery point;
4. the generation is copied outside the primary failure domain where required;
5. the off-host object/archive checksum or provider integrity check passes;
6. retention keeps enough generations without deleting the only proven recovery point;
7. a representative generation has been restored within the drill window.

The default production host currently puts backup generations in the state volume. Monitor the
off-host state archive separately; a green catalog-backup job with no off-host copy still leaves
whole-volume-loss RPO unbounded.

## Alert validation

At installation and at least quarterly:

- stop a disposable API and prove readiness/liveness pages reach the on-call route;
- make a disposable control plane unavailable and prove readiness fails without a restart storm;
- induce a test scheduler failure and confirm event 2000 plus the backup alert;
- fill a disposable filesystem past warning and critical thresholds;
- generate rejected authentication attempts through an isolated test endpoint;
- fail a test CDC receiver and verify consecutive-failure/cursor-lag alerting;
- complete a restore drill and verify the dashboard records its RPO/RTO.

Record alert creation time, notification time, acknowledgement, runbook usability, and cleanup.
Fix missing labels, links, ownership, and deduplication before calling the monitoring gate complete.
