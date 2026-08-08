# Managed data connectors

> **Current delivery state:** the managed connector platform is established, and this feature branch
> adds Kafka Avro alongside the earlier REST, gRPC, PostgreSQL, and HubSpot adapters. The five built-in
> adapters are production paths with full-stack release-gate evidence; a release version is assigned
> only when the branch is released.
> they are not a claim of a broad hosted connector catalogue.

LakeHold's managed-ingestion platform reads bounded full snapshots from REST or gRPC and
checkpointed deltas from PostgreSQL or HubSpot Contacts. It validates a declared data contract and
atomically replaces or key-upserts one DuckLake table. Connector definitions,
schedules, leases, run outcomes, source versions, and row counts are durable PostgreSQL control-plane
state. Response bytes use disposable node-local scratch during a run. LakeHold bounds concurrent
files and aggregate reservations, preserves a configured disk-space floor, creates owner-only files
on Unix, and removes stale crash orphans on startup. A cleanup failure is logged for operators and
never rewrites the durable publication outcome.

This is intentionally a focused, source-level adapter platform, not a Fivetran-sized connector
library. Its built-in catalogue is REST, gRPC, PostgreSQL, HubSpot Contacts, and Kafka Avro. The adapter SDK is
public in `Lakehold.Api`, but it is not yet distributed as a separate package. An operator adapter
must be registered as `IDataConnectorSource`; its manifest id/version then becomes selectable by the
connector API. Built-in ids are defaults, not an allowlist.

## Behaviour

- REST and gRPC refreshes are **full snapshots**. PostgreSQL and HubSpot refreshes are
  **incremental**, reading after the last committed checkpoint and applying a key-based upsert.
- Every record must be a JSON object. Empty full snapshots are refused; an empty incremental read
  is a successful no-op and does not provision or change a target. An adapter may advance a durable
  coverage checkpoint when it has proved that a bounded source window contains no rows.
- `minimumRows`, `requiredColumns`, and `notNullColumns` are evaluated against a temporary table.
  The durable target changes only if every gate passes.
- A failed source read, import, or quality gate leaves the preceding target table unchanged.
- Every claim has an opaque generation token. Immediately before publication the worker verifies
  that generation and holds the PostgreSQL connector row lock until DuckLake publication and the
  durable run transition complete. An expired worker therefore cannot publish over a newer run.
- An incremental adapter can only *propose* a checkpoint. LakeHold advances it after DuckLake has
  committed and while the publication fence is still held. If completion is interrupted, the same
  delta is read again; atomic keyed delete-and-insert makes that replay idempotent.
- A connector exclusively reserves one catalog/schema/table target. Its first publication uses
  create-only semantics and refuses an existing unmanaged table. A non-secret connector ownership
  marker is committed with a newly created table, so replay can recognize that table if PostgreSQL
  completion was interrupted after DuckLake committed; another connector still cannot adopt it.
- Run history is the first source-to-table lineage record: trigger, worker, start/completion times,
  rows read/published, quality result, source version, and a safe bounded error.
- The connector definition also supplies lightweight data-product metadata: owner, description,
  tags, and target table.
- `DELETE` archives a connector: it disables execution but retains the definition, target ownership,
  and immutable run lineage. It does not delete the published table.

## REST source

REST connectors issue an HTTP `GET` and accept either a JSON array or newline-delimited JSON. An
`ETag`, or otherwise `Last-Modified`, is retained as source-version evidence. HTTPS is required by
default.

Create a manual connector with an owner-scoped token:

```bash
curl -X POST "$API/api/tenants/acme/catalogs/analytics/connectors" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "orders-api",
    "description": "Current order snapshot",
    "owner": "supply-chain-data@example.com",
    "tags": ["orders", "operational"],
    "kind": "rest",
    "endpointUrl": "https://source.example.com/v1/orders",
    "authentication": {
      "kind": "bearer",
      "secretReference": "vault://orders-api-token"
    },
    "restResponseFormat": "json-array",
    "targetSchema": "main",
    "targetTable": "orders",
    "minimumRows": 1,
    "requiredColumns": ["id", "updated_at"],
    "notNullColumns": ["id"],
    "enabled": false,
    "refreshIntervalSeconds": null
  }'
```

`authentication.secretReference` names an external secret; LakeHold never accepts or persists its
value. `env://ORDERS_API_TOKEN` remains available for deployment compatibility. Do not put
credentials in the URL; URLs containing user information are rejected, and query strings are
visible connector metadata.

Set `enabled` to `true` and `refreshIntervalSeconds` to 60–31,536,000 for interval scheduling.
Scheduled and manual runs use the same claim, quality, publication, lineage, and audit path.

```bash
curl -X POST \
  "$API/api/tenants/acme/catalogs/analytics/connectors/1/run" \
  -H "Authorization: Bearer $TOKEN"

curl \
  "$API/api/tenants/acme/catalogs/analytics/connectors/1/runs?limit=20" \
  -H "Authorization: Bearer $TOKEN"
```

Manual execution returns `200` only after durable success. Claim/target conflicts return `409`,
quality failures `422`, source/import failures `502`, node scratch exhaustion `503`, and a published
target whose control-plane completion cannot be confirmed `500`. Each response retains the safe run
status, partial rows read, published rows, source version when observed, and bounded error text.

## gRPC source

A gRPC source implements the server-streaming contract in
[`data_connector.proto`](../src/Lakehold.Api/Connectors/Protos/data_connector.proto):

```protobuf
service DataSource {
  rpc Read(ReadRequest) returns (stream DataRecord);
}

message DataRecord {
  string json = 1;
  string source_version = 2;
}
```

Each `json` value is one JSON object. `source_version` is optional, but when supplied it must remain
the same for the entire stream. LakeHold sends the connector name, tenant, and catalog in
`ReadRequest`; a configured bearer token is sent as gRPC `authorization` metadata. Create the
definition exactly as above with `"kind": "grpc"`. `restResponseFormat` is ignored for gRPC.

## Incremental contract and schema policy

Incremental definitions set `readMode` to `incremental`, declare one or more `keyColumns`, and use
one of three schema policies:

- `reject` requires the incoming names and types to remain compatible and refuses added columns.
- `additive` permits new columns and adds them to an existing managed target in the same transaction;
  missing or type-changed existing columns are still refused.
- `mapped-version` applies the declared field mappings and then enforces reject compatibility. It is
  an explicit mapped contract, not automatic coercion.

`fieldMappings` rename top-level JSON properties and optionally apply `trim`, `lowercase`,
`uppercase`, or `to-string`. Unmapped fields are preserved. Mappings are bounded to 256 entries and
cannot execute expressions or user code. They are accepted only with `mapped-version`, so a mapping
cannot be silently applied under a different schema policy.

## PostgreSQL incremental source

The PostgreSQL adapter uses a credential-free `postgresql://host/database` endpoint and a
parameterised `cursor > @checkpoint` predicate. The source table and cursor are bare identifiers;
arbitrary source SQL is not accepted. Supported cursor types are `int64`, `timestamptz`, `uuid`, and
`text`. The definition must explicitly assert `cursorIsCommitMonotonic: true`: LakeHold can check
uniqueness and type, but only the source owner can guarantee that a later commit never receives an
earlier cursor. Do not assert this for random UUIDs, mutable text, or ordinary business timestamps.

```json
{
  "name": "erp-orders",
  "owner": "supply-chain-data@example.com",
  "kind": "postgresql",
  "adapterId": "lakehold.postgresql",
  "adapterVersion": 1,
  "readMode": "incremental",
  "endpointUrl": "postgresql://erp-db.example.com/erp",
  "sourceSettings": {
    "sourceTable": "public.orders",
    "cursorColumn": "change_id",
    "cursorType": "int64",
    "pageSize": 1000,
    "cursorIsCommitMonotonic": true
  },
  "authentication": {
    "kind": "postgresql-password",
    "usernameSecretReference": "vault://erp-reader-username",
    "passwordSecretReference": "vault://erp-reader-password"
  },
  "schemaPolicy": "additive",
  "keyColumns": ["id"],
  "targetSchema": "main",
  "targetTable": "erp_orders",
  "requiredColumns": ["id", "change_id"],
  "notNullColumns": ["id", "change_id"],
  "enabled": true,
  "refreshIntervalSeconds": 60
}
```

Each run reads at most `pageSize` rows. The cursor must be unique across the unread source range;
LakeHold checks and refuses a run if it is not, because advancing a non-unique page boundary could
skip rows. A successful run checkpoints the last ordered cursor; later runs drain the remaining rows
without making one unbounded request. `timestamptz` checkpoints are persisted in round-trip UTC
form. PostgreSQL `timestamp without time zone` is deliberately unsupported because it cannot prove
an unambiguous instant.

## HubSpot Contacts source

The `lakehold.hubspot-contacts` adapter renews an access token from three external references and
reads bounded `lastmodifieddate` windows from the HubSpot CRM search API. It leaves an indexing-delay
margin behind wall-clock time, adaptively narrows any window above 9,000 results, and commits the
fully read window boundary only after publication. The next run overlaps that boundary by 15 minutes
by default, so late-indexed contacts are replayed; keyed upsert removes duplication. Requests share
a node-level rate gate below five requests/second and honour `Retry-After` responses. The endpoint must be
`https://api.hubapi.com`; arbitrary OAuth and API endpoints are not accepted under the HubSpot
adapter name.

```json
{
  "name": "hubspot-contacts",
  "owner": "crm-data@example.com",
  "kind": "hubspot",
  "adapterId": "lakehold.hubspot-contacts",
  "adapterVersion": 1,
  "readMode": "incremental",
  "endpointUrl": "https://api.hubapi.com",
  "sourceSettings": { "pageSize": 200, "properties": ["email", "firstname", "lastname"] },
  "authentication": {
    "kind": "oauth-refresh-token",
    "clientIdSecretReference": "vault://hubspot-client-id",
    "clientSecretReference": "vault://hubspot-client-secret",
    "refreshTokenSecretReference": "vault://hubspot-refresh-token"
  },
  "schemaPolicy": "reject",
  "keyColumns": ["id"],
  "targetSchema": "main",
  "targetTable": "hubspot_contacts",
  "requiredColumns": ["id", "updatedAt"],
  "notNullColumns": ["id", "updatedAt"],
  "enabled": true,
  "refreshIntervalSeconds": 300
}
```

## Secrets and approved authentication

- `env://NAME` reads an injected worker environment variable.
- `vault://key` calls the configured external HTTPS provider at `GET /secrets/{key}` and expects
  `{ "value": "..." }`. Egress is allowlisted and DNS-pinned, the response is bounded, and an
  optional provider bearer token is itself read from an environment variable.
- REST supports bearer, PKCS#12 mTLS (base64 secret plus optional password reference), and custom
  `X-Api-Key` or `Api-Key` headers. gRPC supports bearer metadata. HubSpot accepts only OAuth refresh
  tokens, and PostgreSQL accepts only username/password secret references.

Secret references are durable metadata; resolved values live only in memory. Secret values and
source records are excluded from connector definitions, DTOs, run errors, audit SQL, logs, and
traces.

A secret reference is not authority by itself. Before a tenant owner can save or execute an
authenticated connector, an operator must bind the exact tenant, catalog, reference, and destination
host under `Lakehold:Connectors:SecretBindings`:

```json
{
  "Lakehold": {
    "Connectors": {
      "SecretBindings": [
        {
          "TenantSlug": "acme",
          "CatalogName": "analytics",
          "Reference": "vault://orders-api-token",
          "DestinationHost": "source.example.com"
        }
      ]
    }
  }
}
```

Matching is exact (tenant/catalog/host are case-insensitive; the reference is case-sensitive) and
resolution fails closed. This prevents a tenant owner from naming an arbitrary worker environment
variable or vault key and sending its value to an endpoint they control. Existing authenticated
connector definitions migrated from the legacy environment-variable field also require a binding
before they can be updated or run after this upgrade.

## Retry, pause, resume, and dead letters

Failures use per-connector exponential backoff from `retryBaseSeconds` up to `retryMaxSeconds`.
After `maxAttempts` consecutive failures, the run is `dead-lettered`, the connector is paused, and
scheduled and manual claims are refused until an owner acts:

```text
POST /api/tenants/{tenant}/catalogs/{catalog}/connectors/{id}/pause
POST /api/tenants/{tenant}/catalogs/{catalog}/connectors/{id}/resume
POST /api/tenants/{tenant}/catalogs/{catalog}/connectors/{id}/retry
GET  /api/tenants/{tenant}/catalogs/{catalog}/connectors/{id}/dead-letters?limit=50
```

Each POST body is `{ "version": 7 }`. Pause/resume updates state only. Retry resets the consecutive
failure counter and immediately executes a manual run; its HTTP status uses the same truthful result
mapping as `/run`.

## Egress and resource controls

`Lakehold:Connectors` configures the worker:

| Setting                                  |           Default | Purpose                                                        |
| ---------------------------------------- | ----------------: | -------------------------------------------------------------- |
| `PollInterval`                           |        15 seconds | Due-schedule polling cadence                                   |
| `LeaseDuration`                          |         5 minutes | Durable claim window                                           |
| `RequestTimeout`                         |         2 minutes | Each outbound read or database-command ceiling                 |
| `MaxConcurrentRuns`                      |                 2 | Per-node worker concurrency                                    |
| `MaxSnapshotBytes`                       |           512 MiB | Total response/staging ceiling                                 |
| `MaxRows`                                |         1,000,000 | Records per snapshot                                           |
| `MaxPaginationPages`                     |            10,000 | Maximum pages accepted from a paginated adapter                |
| `MaxHubSpotResultsPerWindow`              |             9,000 | Safety margin below HubSpot's 10,000-result search ceiling      |
| `HubSpotIndexingDelay`                   |         5 minutes | Wall-clock margin left for search indexing                     |
| `HubSpotCheckpointOverlap`               |        15 minutes | Re-read window used to recover late-indexed contacts           |
| `HubSpotMinimumRequestInterval`           | 250 milliseconds | Shared node pacing (four requests/second)                       |
| `MaxRecordBytes`                         |            16 MiB | One JSON object                                                |
| `MaxAggregateScratchBytes`               |             1 GiB | Total connector scratch reserved on one node                   |
| `MinimumFreeBytes`                       |             1 GiB | Filesystem free-space floor preserved by reservations          |
| `StaleFileAge`                           |          24 hours | Startup age threshold for abandoned connector files            |
| `ScratchRoot`                            | OS temporary path | Disposable NDJSON staging root                                 |
| `AllowedHosts`                           |             empty | Optional exact/wildcard hostname allowlist                     |
| `SecretBindings`                         |             empty | Exact operator grants for tenant/catalog/reference/destination |
| `SecretProviderEndpoint`                 |              unset | External HTTPS base URL for `vault://` references               |
| `SecretProviderTokenEnvironmentVariable` |              unset | Environment variable holding the provider bearer token         |

HTTP, redirects to unchecked destinations, URL credentials, loopback/private/link-local/multicast
addresses, and DNS rebinding are refused by default. `AllowHttp` and `AllowUnsafeDestinations` exist
for deliberate development deployments and should remain false in production. If `AllowedHosts` is
non-empty, only exact hosts or entries such as `*.example.com` are reachable.

## Current limitations

- Five built-in adapters only; this is not a broad hosted or partner connector catalogue.
- The adapter SDK is a public source/API contract, not a separately versioned NuGet package.
- PostgreSQL is ordered-poll incremental ingestion, not logical replication or delete capture.
- HubSpot Contacts is the only SaaS adapter. Source-side deletions are not represented. A backlog
  with more than 9,000 contacts in the smallest one-millisecond checkpoint window fails explicitly
  and requires the HubSpot export API; multi-node request contention is bounded by HubSpot's
  `Retry-After` response rather than a distributed LakeHold rate-limit lease.
- Mapping is top-level and declarative; nested-path expressions and arbitrary code are not supported.
- Scheduling is interval-based, not cron-based.
- Connectors are owner-administered through the Workbench, HTTP API, and MCP using the same durable definitions.

### Kafka Avro

`lakehold.kafka-avro` consumes bounded Confluent-wire-format Avro records from Kafka through a
Confluent-compatible HTTPS Schema Registry. LakeHold resolves schemas, stages decoded JSON, and
materialises governed DuckLake/Parquet tables using its normal incremental keyed-upsert path.
Broker and Registry credentials are secret references only; neither raw values nor private CAs are
stored in a connector definition.

Kafka is never contacted directly. Deployment configuration supplies a literal-IP Kafka TCP gateway
which handles every advertised broker listener (and may tunnel via SOCKS), plus a literal-IP HTTP(S)
proxy for Registry traffic. The deployment network policy owns the final allow-list. This is
at-least-once ingestion: after publish, source acknowledgement is durable and explicit retry/replay
is required if the broker commit fails. It is not a generic CDC/Debezium connector, does not support
Apicurio or AWS Glue registries, and makes no exactly-once claim.

Manual `.avro` object-container upload is a separate developer/import path; it is not Kafka wire
format ingestion.
