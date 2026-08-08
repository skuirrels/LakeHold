# Avro File Import Plan

## Decision

Deliver Avro support in two complementary forms: direct import of ordinary Avro object-container files for developer verification, and registry-backed Kafka Avro ingestion for production event streams. Both materialise governed DuckLake tables backed by Parquet.

The first streaming compatibility target is Confluent-compatible Kafka Avro with a registry-backed
schema decoder. It does not yet add Apicurio or AWS Glue registry modes, Debezium source-log CDC,
or a general direct HTTP event-posting contract.

## User outcome

An authorised catalog user can import an `.avro` file through LakeHold's existing tabular-import workflow for testing and ad-hoc use. An operator can configure a Kafka source that consumes Avro records from a topic, resolves the writer schema through a supported registry, validates the records, and materialises a governed table.

The truthful product claim after release will be: **LakeHold supports governed Avro file ingestion
and Confluent-compatible Kafka Avro ingestion into DuckLake/Parquet tables.**

## Scope

- Support standard Avro object-container files with their embedded writer schema.
- Reuse the existing authenticated upload, tenant/catalog authorisation, scratch-space limits, conflict policy, table-creation flow, audit evidence, and error-redaction behaviour.
- Add a registry-backed Kafka Avro connector that reuses the connector platform's ownership, run evidence, retry, quality-gate, and safe-publication model.
- Deliver connector administration as a first-class control-plane experience in both the LakeHold UI and MCP server. Administrators must be able to create, inspect, edit, save, run, pause, resume, retry, and retire multiple connector definitions across the catalogs they administer.
- Load the file through DuckDB's signed Avro extension and materialise the result into a DuckLake table.
- Surface the imported table through the existing catalog and query experiences.
- Add support only where it can be packaged for every LakeHold target platform with the same DuckDB version as the engine.

## Explicit non-goals

- Apicurio or AWS Glue schema-registry compatibility.
- Debezium-specific source-log contracts or a claim of generic CDC support.
- Avro change-event semantics, ordered replay, source checkpoints, or CDC.
- Treating Avro as DuckLake's physical table-storage format.
- Claiming support for every Avro feature before it is tested.

## Delivery steps

### 1. Extension packaging and safe loading

1. Add the DuckDB Avro extension artifact to LakeHold's existing signed, version-pinned extension packaging process.
2. Load it only for the trusted import execution path.
3. Keep `read_avro` unavailable to untrusted external SQL planning or arbitrary remote access paths.
4. Confirm offline container startup does not need to fetch an extension at runtime.

### 2. Direct import contract

1. Add `.avro` to the accepted tabular-import file types.
2. Detect the format from the controlled upload path rather than accepting an arbitrary DuckDB expression.
3. Read the embedded Avro writer schema and present the resulting columns through the existing preview/validation contract.
4. Create the target using a controlled `read_avro` import operation, then let DuckLake persist the governed table as Parquet.
5. Refuse unsafe names, conflicting targets, malformed files, unsupported codecs, or schema shapes that cannot be represented safely.

### 3. Kafka Avro source adapter

1. Add a `kafka-avro` connector kind alongside the existing REST, gRPC, PostgreSQL, and HubSpot source adapters.
2. Let an operator configure broker endpoints, topic, consumer group, authentication, registry endpoint, target catalog/table, and quality rules through deployment-owned secret references.
3. Decode registry-backed Avro wire records using the selected schema-registry client; do not route them through DuckDB's `read_avro` file reader.
4. Retain topic, partition, offset, schema identity, and connector-run lineage for every accepted batch.
5. Process bounded record windows, publish their resulting DuckLake changes, and advance consumer progress only after durable publication.
6. Provide pause, resume, retry, bounded replay, dead-letter handling, and safe error evidence.
7. Require a deployment-owned egress gateway before a Kafka connector can be saved or run: a
   literal-IP Kafka TCP gateway that owns every permitted broker route (including advertised
   listeners), and a literal-IP HTTP(S) proxy for Schema Registry traffic. The TCP gateway may
   tunnel through SOCKS, but that is deployment infrastructure rather than an unsupported Kafka
   client configuration.

#### Kafka egress security guarantee and operational constraint

LakeHold rejects Kafka Avro configuration when the named gateway policy and both literal-IP
gateway endpoints are absent. The worker connects only to the Kafka TCP gateway; that gateway
must map every advertised broker listener and enforce the policy's permitted routes. Every Schema
Registry request uses the separate HTTP(S) proxy. The deployment must allow the LakeHold workload
to reach only these gateways; the gateway policy must itself restrict broker, registry, DNS, and
CONNECT/SOCKS destinations. A private Registry CA is supplied only through deployment configuration
and uses the client-supported CA-bundle setting; certificate verification is never disabled.

The gateway is a second control, not a replacement for the shared egress policy. Like every other
adapter, this one resolves `OutboundDestinationPolicy` — the operator's host allow-list and the
private-address checks — for each broker in the bootstrap list and for the Schema Registry, at
creation and again on every read (invariant 23). Because the policy is what approves a destination,
a connector's `endpointUrl` must name the same host and port as its `schemaRegistryUrl`: letting the
two disagree would have the policy approve one host while the adapter read another.

### 4. Administrator configuration, UI, and MCP parity

Connector definitions are durable control-plane records, not hidden application settings. The same validated definition and lifecycle service must be used by the UI, HTTP administration API, and MCP tools so a connector created by an AI agent appears immediately in the UI, and a connector created in the UI can be inspected and operated through MCP.

1. Provide an administrator-only connector inventory that can show all connectors the administrator is entitled to manage, including their source type, tenant, target catalog/table, owner, enabled or paused state, last outcome, freshness, and current failure status.
2. Provide a create and edit experience that lets an administrator select the connector type, configure its source and schema registry, select the target catalog and table, define mappings and quality rules, set run/retry behaviour, validate the configuration, then save it durably.
3. For Kafka Avro, surface broker endpoints, topic, consumer group, authentication mode, schema-registry compatibility mode and endpoint, schema policy, target, and run controls. Show secret *references* only; never expose raw broker, registry, or source credentials in UI, API, MCP responses, logs, or browser state.

#### Current administration-surface limit

The connector UI currently renders the built-in adapter forms (REST, gRPC, PostgreSQL, HubSpot,
and Kafka Avro) explicitly. The API and MCP already validate adapter manifests, so a registered
operator adapter remains manageable there, but it does not automatically gain a generated browser
form. A future adapter manifest must add declarative UI field metadata before the UI can become
fully manifest-driven; do not imply that arbitrary adapters are browser-configurable today.
4. Provide connector detail views with configuration summary, run history, checkpoints or consumer progress, safe diagnostics, quality-gate outcome, and dead-letter/retry controls.
5. Add MCP tools with the same administrator authorisation and validation rules: list connectors, read a connector, create, update, validate, run, pause, resume, retry, and inspect run/dead-letter evidence.
6. Attribute every configuration and lifecycle action to its authenticated human or MCP actor, retain an auditable change history, and use optimistic concurrency so an agent cannot silently overwrite a concurrent administrator edit.
7. Make the UI and MCP display only deployment-owned secret-binding names. Secret values must be provisioned outside LakeHold's UI and MCP flow.

### 5. Guardrails and diagnostics

1. Apply the existing maximum-upload, scratch-space, time, and resource controls.
2. Produce safe, actionable user errors without exposing storage paths, secrets, or raw extension failures.
3. Record import lineage: source file metadata, inferred table schema, import mode, result, and any safe failure evidence.
4. Document the difference between Avro file import and registry-backed Kafka Avro.

### 6. Verification

Cover:

- A valid primitive-record file and a multi-record file.
- Logical types and supported nested values.
- Schema evolution represented by separate input files.
- Malformed files, unsupported recursive schemas or codecs, and oversized files.
- Authorisation, tenant isolation, target conflicts, cleanup after failure, and no unbounded scratch growth.
- Packaged execution on every supported LakeHold platform.
- A smoke demonstration: upload an Avro file, create a table, query it, and confirm its durable DuckLake/Parquet representation.
- A Kafka demonstration: consume compatible Avro records from a protected topic, materialise a governed table, restart safely, and prove replay does not create duplicate effects.
- UI administration: create and save several connectors targeting different catalogs, confirm inventory, detail, lifecycle controls, and run evidence remain correct after refresh.
- MCP parity: create or update a connector through MCP, confirm it is visible and operable in the UI, then make a non-conflicting UI edit and verify the MCP view reflects it.
- Security: non-administrators cannot view or alter connectors outside their authority; raw credentials never appear in UI, API, MCP, logs, run evidence, or browser state.
- Kafka egress: direct Kafka and Schema Registry configuration is rejected; a valid gateway
  configuration routes both protocol clients through the deployment-owned egress boundary.
- Docker protocol fixture: Confluent Kafka with its advertised listener, a Schema Registry using
  Jetty JAAS/property-file BASIC authentication behind trusted HTTPS, a Kafka TCP gateway that
  tunnels through SOCKS5, and a separate HTTP registry proxy. It runs LakeHold's source adapter
  rather than a console consumer. It runs **only inside that Linux Compose fixture**, started by
  `scripts/test-kafka-avro-proxy.sh` and CI, because the registry leg is TLS against a fixture CA
  that exists only in the container's trust store. There is deliberately no host-side fallback: one
  that skipped the TLS leg would report the same green tick for strictly less evidence.
- Tombstone handling, in the same fixture: the topic opens with a null-valued record at offset 0, so
  a bounded read has to pass it and still return the Avro record behind it, with the checkpoint
  advanced past both. It is a fixture case rather than a unit test because only a real broker
  produces a tombstone, and the failure it guards — a connector that fails the same record on every
  replay and never moves again — is invisible until one arrives.

## Acceptance criteria

- An authorised user can import a supported `.avro` object-container file into a selected LakeHold catalog.
- An operator can configure a protected Kafka Avro source with registry-backed schema resolution through the managed connector platform.
- An administrator can manage multiple connector definitions through the UI and MCP with consistent validation, lifecycle controls, audit attribution, tenant/catalog authorisation, and non-secret configuration visibility.
- The created table is queryable through LakeHold and durable as a normal DuckLake/Parquet table.
- Unsupported or unsafe input fails without publishing a partial table or exposing sensitive details.
- The feature works in the packaged runtime without downloading extensions.
- Documentation and marketing language distinguish Avro file ingestion from the supported
  Confluent-compatible Kafka Avro connector, and do not claim generic CDC support.

## Review gate before implementation

The first schema-registry compatibility target is **Confluent-compatible**. Apicurio, AWS Glue,
direct HTTP posting, and non-Kafka brokers remain follow-on adapters; they should share the same
canonical change model rather than define new table-publication semantics.
