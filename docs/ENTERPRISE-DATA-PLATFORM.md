# LakeHold as an Enterprise Data Platform

LakeHold is evolving from a self-hosted lakehouse into a focused Enterprise Data Platform (EDP):
one governed place to acquire, store, understand, serve, and operate organisational data. The goal
is not to imitate every service in Databricks or Snowflake. It is to give .NET and lean data teams a
smaller, private platform with open storage, explicit operations, and an exit path they can prove.

> **Current boundary:** LakeHold already supplies a strong lakehouse. The first managed
> full-snapshot connector foundation is implemented and verified in source for the next release, but
> is not included in the current v1.2.0 artifact. LakeHold is not yet a complete EDP. Incremental
> adapters, searchable governance, end-to-end lineage, a semantic layer, mature BI compatibility,
> and an adapter ecosystem remain planned work.

## What an EDP does

An Enterprise Data Platform normally brings six responsibilities together:

| Responsibility    | Typical use                                                                   | LakeHold direction                                                                     |
| ----------------- | ----------------------------------------------------------------------------- | -------------------------------------------------------------------------------------- |
| Acquire           | Bring operational, SaaS, file, event, and database data into governed storage | Managed connectors, browser imports, CDC, and future incremental adapters              |
| Store and process | Keep durable analytical data and execute transformations and queries          | DuckLake tables, open Parquet, DuckDB compute, snapshots, and saved SQL                |
| Govern            | Define ownership, contracts, quality, classification, policy, and lineage     | Connector contracts in current source; searchable asset governance and lineage planned |
| Serve             | Make trusted data available to applications, analysts, BI, and AI             | HTTP, PostgreSQL wire, EF Core, MCP, and future semantic/open-engine interfaces        |
| Operate           | Schedule, observe, recover, optimise, and prove service health                | Leases, audit, telemetry, maintenance, backup/restore, and verified eject              |
| Secure            | Enforce identity, tenant boundaries, secrets, and controlled egress           | Scoped tokens, OIDC, roles, DNS-pinned egress, and external secret providers planned   |

## What LakeHold provides

The distinction below is deliberate: the lakehouse capabilities are available in v1.2.0, while
managed connectors are implemented in source for the next release but are not yet in a published
artifact.

### Available on the main branch: governed lakehouse foundation

- PostgreSQL control and DuckLake metadata with DuckDB execution and open Parquet data.
- Tenant/catalog identity, scoped API tokens, OIDC, owner/editor/reader roles, and audit history.
- Atomic table publication, snapshots and time travel, saved-query publication, maintenance, and
  multi-node leases.
- Backup/restore and signed, row-count-attested eject bundles that prove the exit path.

### Implemented in source for the next release: managed ingestion

- REST JSON-array and NDJSON full-snapshot connectors.
- A small server-streaming gRPC full-snapshot contract.
- Durable connector definitions, interval schedules, manual runs, run lineage, safe failure evidence,
  quality gates, target ownership, and fenced publication.

The managed connector contract and operator settings are documented in
[`CONNECTORS.md`](CONNECTORS.md).

### Available on the main branch: data movement

- Browser-local CSV and XLSX imports with bounded, owner-only scratch space.
- Typed CDC pull and signed at-least-once webhooks for downstream change consumption.

### Available on the main branch: consumption

- Browser Workbench, HTTP APIs, MCP resources/tools, EF Core integration, and direct SQL through the
  PostgreSQL wire endpoint.
- `psql`, DBeaver, and Npgsql work through the wire endpoint. Power BI remains blocked by the
  documented PostgreSQL type-catalogue compatibility gap.

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

## What is not complete

LakeHold should not yet be presented as a broad, finished EDP. The following capabilities remain
open:

- Incremental source checkpoints and resumable database/SaaS connectors.
- A versioned adapter SDK, adapter manifest, mappings, transforms, and explicit schema-evolution
  decisions.
- OAuth renewal, mTLS, and an external secret-provider abstraction.
- Searchable enterprise catalog, column classification, policy administration, freshness status,
  and navigable upstream/downstream lineage.
- Governed metrics and semantic models.
- Power BI compatibility, a supported JDBC/ODBC strategy, and live open-engine access through an
  Iceberg-compatible catalog.
- A connector administration experience in the Workbench.
- Connector-specific retry/backoff, service objectives, alerting, and resource/cost reporting.

## Delivery plan and status

The status-controlled plan is maintained in
[`ENTERPRISE-DATA-PLATFORM-ROADMAP.md`](ENTERPRISE-DATA-PLATFORM-ROADMAP.md). It separates source
implementation from capabilities available in a published release and work that remains
unimplemented.
