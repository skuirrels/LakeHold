# LakeHold as an Enterprise Data Platform

LakeHold is evolving from a self-hosted lakehouse into a focused Enterprise Data Platform (EDP):
one governed place to acquire, store, understand, serve, and operate organisational data. The goal
is not to imitate every service in Databricks or Snowflake. It is to give .NET and lean data teams a
smaller, private platform with open storage, explicit operations, and an exit path they can prove.

> **Current boundary:** LakeHold v1.3.0 includes the managed connector platform. The current source
> also implements the `/api/v1` public API foundation and generated Java, Go, .NET, and Python SDK
> candidates, but those changes are not a released server or publicly installed packages.
> Searchable governance, end-to-end lineage, a semantic layer, mature BI compatibility, full SDK
> convenience/conformance coverage, and a broad adapter ecosystem remain incomplete.

## What an EDP does

An Enterprise Data Platform normally brings six responsibilities together:

| Responsibility    | Typical use                                                                   | LakeHold direction                                                                     |
| ----------------- | ----------------------------------------------------------------------------- | -------------------------------------------------------------------------------------- |
| Acquire           | Bring operational, SaaS, file, event, and database data into governed storage | Full and incremental managed connectors, browser imports, and CDC                      |
| Store and process | Keep durable analytical data and execute transformations and queries          | DuckLake tables, open Parquet, DuckDB compute, snapshots, and saved SQL                |
| Govern            | Define ownership, contracts, quality, classification, policy, and lineage     | Shipped connector contracts; searchable asset governance and lineage planned            |
| Serve             | Make trusted data available to applications, analysts, BI, and AI             | Versioned HTTP, PostgreSQL wire, EF Core, MCP, source SDK candidates, and planned open-engine interfaces |
| Operate           | Schedule, observe, recover, optimise, and prove service health                | Leases, audit, telemetry, maintenance, backup/restore, and verified eject              |
| Secure            | Enforce identity, tenant boundaries, secrets, and controlled egress           | Scoped tokens, OIDC, roles, DNS-pinned egress, and external secret references          |

## What LakeHold provides

The distinction below is deliberate: v1.3.0 ships the governed lakehouse and managed connector
platform, while the broader EDP capabilities remain partial or planned.

### Available on the main branch: governed lakehouse foundation

- PostgreSQL control and DuckLake metadata with DuckDB execution and open Parquet data.
- Tenant/catalog identity, scoped API tokens, OIDC, owner/editor/reader roles, and audit history.
- Atomic table publication, snapshots and time travel, saved-query publication, maintenance, and
  multi-node leases.
- Backup/restore and signed, row-count-attested eject bundles that prove the exit path.

### Shipped in v1.3.0: managed ingestion platform

- REST JSON-array and NDJSON full-snapshot connectors.
- A small server-streaming gRPC full-snapshot contract.
- Durable connector definitions, interval schedules, manual runs, run lineage, safe failure evidence,
  quality gates, target ownership, and fenced publication.
- A public versioned adapter contract, durable incremental checkpoints, and replay-safe keyed upsert.
- PostgreSQL typed-cursor ingestion and OAuth-backed HubSpot Contacts ingestion.
- Exponential retry/backoff, pause/resume/immediate retry, dead letters, mappings, bounded
  transforms, and explicit schema policy.
- `env://` and external HTTPS `vault://` secret providers, OAuth renewal, bearer auth, PKCS#12 mTLS,
  PostgreSQL credentials, allowlisted API-key headers, and operator-owned tenant/catalog/host secret
  bindings that prevent tenant-authored credential exfiltration.
- Commit-monotonic PostgreSQL polling contracts and adaptively windowed, rate-paced HubSpot search
  ingestion below the provider's 10,000-result ceiling.

The managed connector contract and operator settings are documented in
[`CONNECTORS.md`](CONNECTORS.md).

### Available on the main branch: data movement

- Browser-local CSV and XLSX imports with bounded, owner-only scratch space.
- Typed CDC pull and signed at-least-once webhooks for downstream change consumption.

### Implemented consumption surfaces

- Browser Workbench, HTTP APIs, MCP resources/tools, EF Core integration, and direct SQL through the
  PostgreSQL wire endpoint.
- `psql`, DBeaver, and Npgsql work through the wire endpoint. Power BI remains blocked by the
  documented PostgreSQL type-catalogue compatibility gap.
- Canonical `/api/v1` routes, production OpenAPI, RFC 9457 errors, cursor pages, idempotent bounded
  mutations, durable long-running operations, and capability discovery are implemented in source.
- Generated Java, Go, .NET, and Python clients cover the frozen v1 contract and have authentication,
  reliability, pagination, operation-waiter, and streaming conformance tests. They remain source
  candidates rather than released packages; released-server conformance and registry publication are
  still open.
- The older source-only `Lakehold.Client` project still supports the first-party .NET replication
  worker and is separate from the new general-purpose .NET SDK candidate.

## Typical enterprise uses

- Consolidate departmental APIs and extracts into governed analytical tables.
- Publish reusable data products with an owner, description, tags, contract, quality evidence, and
  retained refresh history.
- Give applications, analysts, SQL tools, and AI agents access to the same catalog and authorization
  decisions.
- Operate a private lakehouse in a VM, Kubernetes, an air-gapped network, or infrastructure governed
  by organisational data-residency policy.
- Replace opaque warehouse lock-in with open Parquet, ordinary SQL metadata, tested backup/restore,
  and a signed export path.

## Roadmap

LakeHold should not yet be presented as a broad, finished EDP. The following capabilities remain
open:

- A broad, separately distributed and production-certified database/SaaS adapter ecosystem. The
  current in-process connector adapter contract is part of the API assembly and the built-in
  catalogue contains four adapters; it is distinct from the planned client SDKs.
- Searchable enterprise catalog, column classification, policy administration, freshness status,
  and navigable upstream/downstream lineage.
- Governed metrics and semantic models.
- Power BI compatibility, a supported JDBC/ODBC strategy, and live open-engine access through an
  Iceberg-compatible catalog.
- A connector administration experience in the Workbench.
- Connector service objectives, alerting, and resource/cost reporting.
- Full released-server SDK conformance coverage and publicly signed Java, Go, .NET, and Python
  packages. The semantic OpenAPI compatibility gate and source SDK conveniences are implemented;
  the remaining acceptance gates and exact source-versus-release boundary are tracked in the
  delivery plan.

## Delivery plan and status

The status-controlled plan is maintained in
[`ENTERPRISE-DATA-PLATFORM-ROADMAP.md`](ENTERPRISE-DATA-PLATFORM-ROADMAP.md). It separates source
implementation from capabilities available in a published release and work that remains
unimplemented.
