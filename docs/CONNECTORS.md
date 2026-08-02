# Managed data connectors

> **Delivery state — 2 August 2026:** this capability is implemented, committed, and verified in
> source for the next LakeHold release. It is not included in the current v1.2.0 release. The
> contracts below document the source implementation; operators should build current source or wait
> for a later published artifact before relying on it.

LakeHold's first managed-ingestion surface reads a bounded full snapshot from REST or gRPC,
validates a small data contract, and atomically replaces one DuckLake table. Connector definitions,
schedules, leases, run outcomes, source versions, and row counts are durable PostgreSQL control-plane
state. Response bytes use disposable node-local scratch during a run. LakeHold bounds concurrent
files and aggregate reservations, preserves a configured disk-space floor, creates owner-only files
on Unix, and removes stale crash orphans on startup. A cleanup failure is logged for operators and
never rewrites the durable publication outcome.

This is intentionally a simple interoperability contract, not a Fivetran-sized connector library.
Incremental cursors, transforms, schema-evolution policy, database/SaaS adapters, and a connector SDK
are follow-on work in [`ENTERPRISE-DATA-PLATFORM-ROADMAP.md`](ENTERPRISE-DATA-PLATFORM-ROADMAP.md).

## Behaviour

- Each refresh is a **full snapshot**. A successful run replaces the target table; it does not append.
- Every record must be a JSON object. Empty snapshots are refused because LakeHold cannot infer a
  replacement schema from them.
- `minimumRows`, `requiredColumns`, and `notNullColumns` are evaluated against a temporary table.
  The durable target changes only if every gate passes.
- A failed source read, import, or quality gate leaves the preceding target table unchanged.
- Every claim has an opaque generation token. Immediately before publication the worker verifies
  that generation and holds the PostgreSQL connector row lock until DuckLake publication and the
  durable run transition complete. An expired worker therefore cannot publish over a newer run.
- A connector exclusively reserves one catalog/schema/table target. Its first publication uses
  create-only semantics and refuses an existing unmanaged table; only a successfully provisioned
  target can subsequently be replaced.
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
    "credentialEnvironmentVariable": "ORDERS_API_TOKEN",
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

`credentialEnvironmentVariable` is the **name** of a worker environment variable containing a
bearer token. LakeHold never accepts or persists the token value. Do not put credentials in the URL;
URLs containing user information are rejected, and query strings are visible connector metadata.

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

## Egress and resource controls

`Lakehold:Connectors` configures the worker:

| Setting                    |           Default | Purpose                                               |
| -------------------------- | ----------------: | ----------------------------------------------------- |
| `PollInterval`             |        15 seconds | Due-schedule polling cadence                          |
| `LeaseDuration`            |         5 minutes | Durable claim window                                  |
| `RequestTimeout`           |         2 minutes | REST/gRPC source-read ceiling                         |
| `MaxConcurrentRuns`        |                 2 | Per-node worker concurrency                           |
| `MaxSnapshotBytes`         |           512 MiB | Total response/staging ceiling                        |
| `MaxRows`                  |         1,000,000 | Records per snapshot                                  |
| `MaxRecordBytes`           |            16 MiB | One JSON object                                       |
| `MaxAggregateScratchBytes` |             1 GiB | Total connector scratch reserved on one node          |
| `MinimumFreeBytes`         |             1 GiB | Filesystem free-space floor preserved by reservations |
| `StaleFileAge`             |          24 hours | Startup age threshold for abandoned connector files   |
| `ScratchRoot`              | OS temporary path | Disposable NDJSON staging root                        |
| `AllowedHosts`             |             empty | Optional exact/wildcard hostname allowlist            |

HTTP, redirects to unchecked destinations, URL credentials, loopback/private/link-local/multicast
addresses, and DNS rebinding are refused by default. `AllowHttp` and `AllowUnsafeDestinations` exist
for deliberate development deployments and should remain false in production. If `AllowedHosts` is
non-empty, only exact hosts or entries such as `*.example.com` are reachable.

## Current limitations

- Full-snapshot refresh only; no incremental cursor or CDC checkpoint.
- Bearer-token authentication only; no OAuth refresh, mTLS client identity, or custom headers.
- Source records are ingested as returned; there is no mapping or transform step.
- Schema is inferred per successful snapshot. Required columns protect selected contract fields,
  but there is no explicit compatible-schema evolution policy yet.
- Scheduling is interval-based, not cron-based, and retries occur on the next interval or manual run.
- Connectors are owner-administered over HTTP; the Workbench has no connector UI yet.
