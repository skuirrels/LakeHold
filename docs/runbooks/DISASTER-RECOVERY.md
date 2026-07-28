# Disaster-recovery runbook

This runbook restores service after durable-state loss. It is deliberately separate from
[the exit path](../EXIT-PATH.md): the exit document explains DuckLake storage mechanics, while this
document defines decisions, approvals, order of operations, validation, and drills.

## Recovery boundary

LakeHold has several kinds of state, and no single built-in catalog backup protects all of them:

| State | Primary protection | Catalog backup protects it? |
|---|---|---|
| Tenants and catalog descriptors | PostgreSQL control-plane backup/PITR | No |
| API token hashes and roles | PostgreSQL; plaintext tokens must also be in a secret store | No |
| Subscriptions, delivery cursors, query history | PostgreSQL control-plane backup/PITR | No |
| Legacy local DuckLake metadata and history | Complete catalog backup plus surviving data | Yes |
| Local Parquet data and delete files | Consistent full-state archive | No |
| External object-store data | Object-store versioning/replication/backup | No |
| PostgreSQL DuckLake metadata | PostgreSQL backup/PITR; catalog backup is a portable exit copy | Partly; not automatic failover |
| Backup generations and eject bundles | Off-host copy or remote storage | They are themselves artifacts |
| Configuration and secrets | Deployment/configuration repository and secret store | No |

In the packaged production API, `StateRoot` resolves local data, backup, and eject roots beneath one
state volume. PostgreSQL control-plane and DuckLake metadata sit outside that volume. A default
catalog backup can still share a node/volume failure domain with local Parquet; export it off-host
and protect PostgreSQL independently.

## Define and prove recovery objectives

The operator owns the approved recovery-point objective (RPO) and recovery-time objective (RTO).
Defaults are exposure bounds, not promises:

| Failure | What bounds data loss | Default posture |
|---|---|---|
| API or workbench container loss | Durable volume remains | No data loss expected; restart time is the RTO |
| Local catalog metadata loss, data and backups survive | Latest complete hourly catalog backup | Up to one backup interval of catalog history and later writes |
| Catalog metadata loss with no usable backup | Last flush plus forensic reconstruction limits | Inlined writes can be lost; history and liveness can be unrecoverable |
| Whole host or state-volume loss | Latest verified off-host full-state archive | **Unbounded until the operator schedules and exports one** |
| External object-store loss | Provider versioning/replication/backup policy | Outside LakeHold |
| PostgreSQL metadata loss | PostgreSQL PITR/dump policy | Outside LakeHold |

The default 15-minute flush cadence only bounds inlined data that would be permanently absent from a
forensic reconstruction. It does not turn Parquet alone into an exact recovery source: delete-file
application, file liveness, history, views, and other catalog state still require metadata.

Set RPO and RTO after timing a restore with production-representative size and topology. Record:

- data and metadata size;
- backup frequency and off-host copy completion time;
- restore start, readiness, authenticated canary, and integrity-validation times;
- actual data point recovered;
- deviations and owners.

## Preconditions

Maintain these before an incident:

- a pinned `compose.production.yaml`, release tag, and image digest;
- a consistent state archive and SHA-256 checksum in a different failure domain;
- versioning or backup for external object storage and PostgreSQL;
- an inventory mapping tenants, catalogs, metadata kind, data location, and required credentials;
- an owner token and instance token in an approved secret store;
- configuration, TLS material, object-store/database credentials, signing keys, and webhook secrets
  recoverable independently of the failed node;
- enough isolated capacity to restore without overwriting production;
- monthly catalog restore tests and quarterly full-node drills.

LakeHold stores only API-token hashes. Restoring PostgreSQL without the corresponding plaintext
break-glass token can restore data while leaving operators unable to administer it. Setting
`Lakehold__BootstrapToken` does not add a new token when restored token records already exist. Do not
delete token rows to force bootstrap.

## Choose the recovery path

| Observation | Recovery path |
|---|---|
| Containers failed; state volume is intact and readable | Restart or image rollback through incident response |
| Bad application deploy; state was not changed incompatibly | Redeploy last-known-good pinned tag |
| Whole node/volume lost; PostgreSQL survives | [Full-state recovery](#full-state-recovery) |
| PostgreSQL control plane failed | Restore PostgreSQL with its native PITR/dump process before starting LakeHold |
| One local catalog metadata file is missing or corrupt; data and complete backup survive | [Catalog metadata recovery](#catalog-metadata-recovery) |
| PostgreSQL metadata service failed | Restore PostgreSQL with its native PITR/dump process |
| External object-store data failed | Restore/version-recover the object store first |
| Only raw Parquet survives and no complete metadata backup exists | Forensic fallback in the exit-path document; not normal recovery |
| A user changed or deleted rows but catalog is healthy | Use time travel and a new corrective commit, not disaster recovery |

If scope or integrity is uncertain, restore to isolation and preserve the original state. Never merge
an archive into a partially populated volume.

## Create a consistent full-state archive

The safest current full-state copy stops writers while the volume is archived:

```bash
ARCHIVE="lakehold-state-$(date -u +%Y%m%dT%H%M%SZ).tar.gz"
make stop
make backup-state ARCHIVE="$ARCHIVE"
sha256sum "$ARCHIVE" > "$ARCHIVE.sha256"
LAKEHOLD_TAG=<pinned-current-tag> make deploy
```

Copy the archive and checksum to approved off-host storage, then verify the stored copy. The archive
created by `make backup-state` remains in the working directory until moved; leaving it beside the
Compose file is not off-host protection.

This procedure has downtime for local data/artifacts. It does not back up PostgreSQL or remote object
storage. Coordinate their provider-native recovery points with the volume/object-store policy, then
prove the combination with the same restore drill.

Record the tag/digest, archive checksum, UTC start/end, volume name, copy destination, operator, and
whether the off-host verification passed.

## Full-state recovery

Use this for node/local-volume loss after the PostgreSQL and object-storage recovery points are
confirmed. The procedure assumes a fresh, isolated recovery host. If the expected volume already
exists, stop: preserve it and recover on another host or under an approved isolated Compose
configuration.

### 1. Declare and prepare

- Open an incident, freeze writes and deployment automation, and record the selected recovery point.
- Obtain the pinned Compose file, exact image tag/digests, archive, checksum, configuration, and
  secrets from independent stores.
- Confirm external PostgreSQL and object-store dependencies are restored to a compatible point.
- Keep the replacement node isolated from normal ingress until validation completes.

### 2. Verify the archive

```bash
sha256sum -c lakehold-state-<timestamp>.tar.gz.sha256
tar -tzf lakehold-state-<timestamp>.tar.gz | sed -n '1,80p'
```

The archive should include the expected local `data/`, `backups/`, and `ejects/` trees. It must not
be treated as the PostgreSQL backup. An absent directory can be legitimate only if the inventory
says that state was external. A checksum pass proves transfer integrity, not application
consistency.

### 3. Restore into an empty volume

The production Compose project is named `lakehold`, so its expected volume is
`lakehold_lakehold-state`:

```bash
docker volume inspect lakehold_lakehold-state
```

The command must report that the volume does not exist on a fresh recovery host. Then:

```bash
LAKEHOLD_TAG=<pinned-tag> \
  docker compose -f compose.production.yaml create
docker run --rm \
  -v lakehold_lakehold-state:/state \
  -v "$PWD":/archive:ro \
  alpine tar xzf /archive/lakehold-state-<timestamp>.tar.gz -C /state
```

`docker compose create` creates the correctly labelled empty volume and stopped containers; it does
not start the application. Do not add a command that empties an existing volume. Recovery should
fail safely when the target is not empty.

### 4. Start the exact release

Restore secrets through the platform secret store, then:

```bash
LAKEHOLD_TAG=<pinned-tag> \
  docker compose -f compose.production.yaml up -d --wait --wait-timeout 180
docker compose -f compose.production.yaml ps
curl --fail --silent --show-error \
  "http://127.0.0.1:${LAKEHOLD_PORT:-8080}/health"
```

Do not upgrade during recovery. Recover the known state on the matching release first; upgrade in a
separate change after closure.

### 5. Validate before traffic

Use a stored break-glass token and the inventory:

1. List tenants and confirm no unexpected tenant or catalog is present.
2. For every critical catalog, run a read-only canary with an expected row count and aggregate.
3. Compare the latest snapshot and known business watermark with the selected recovery point.
4. List backup generations and confirm complete manifests remain readable.
5. Verify token roles and revocations, subscriptions/cursors, and required audit history.
6. Confirm maintenance schedules are logged and a non-destructive manual backup succeeds.
7. Confirm external data paths and PostgreSQL metadata attach read-only before allowing writes.
8. Confirm metrics, traces, logs, dashboards, and paging resumed.

Health alone is insufficient: it tests control-plane connectivity, not each tenant catalog or its
row-level integrity.

### 6. Return to service

Record validation evidence and incident-commander approval. Reintroduce read traffic first, watch
errors and latency, then enable writes. Keep the failed node and original artifacts isolated until
the evidence-retention owner releases them.

Immediately create and export a new consistent backup after the service stabilizes.

## Catalog metadata recovery

Use this only when:

- the control plane is intact;
- one local metadata file is missing or known corrupt;
- its data path survives;
- a complete catalog backup is readable;
- the catalog name is unambiguous in the supported one-tenant profile.

Catalog restore rebuilds DuckLake metadata. It does not restore table data, control-plane records,
tokens, subscriptions, or audit history.

The API examples below expect `LAKEHOLD_URL`, `TENANT`, `CATALOG`, and `LAKEHOLD_TOKEN` as defined
in the [operations hub](../OPERATIONS.md#routine-operations).

### 1. Contain and preserve

Block writes to the affected catalog. Capture the latest known snapshot and failure time. If the
metadata file exists but is corrupt, stop the API and move the file to a quarantined name on the
same volume; do not delete it. For a default catalog named `analytics`, the control-plane descriptor
points at `/var/lib/lakehold/catalogs/analytics.ducklake`.

Any filesystem move is an incident-commander-approved recovery action. Record the source and
quarantine names exactly.

### 2. Select a complete generation

Start the API without normal user ingress if it was stopped, then:

```bash
curl --fail --silent --show-error \
  -H "Authorization: Bearer $LAKEHOLD_TOKEN" \
  "$LAKEHOLD_URL/api/tenants/$TENANT/catalogs/$CATALOG/backups"
```

Choose the newest generation with `"complete": true` at or before the required recovery point. A
generation without a manifest is never restorable.

### 3. Restore to the descriptor's missing path

For the default local layout, a bare target file name resolves under the metadata root:

```bash
curl --fail --silent --show-error -X POST \
  -H "Authorization: Bearer $LAKEHOLD_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"generation":"<generation>","targetMetadataPath":"'"$CATALOG"'.ducklake"}' \
  "$LAKEHOLD_URL/api/tenants/$TENANT/catalogs/$CATALOG/backups/restore"
```

Restore refuses to overwrite. It creates a new file, loads every metadata table from Parquet,
attaches it read-only against the surviving data path, and verifies that snapshots can be read.

If the catalog uses a custom path, same-named catalogs, or PostgreSQL metadata, stop. Restoring to an
arbitrary new file does not update the control-plane descriptor, and the current API has no supported
repoint operation. Use full-state or native PostgreSQL recovery rather than editing
the shared control-plane tables under pressure.

### 4. Evict stale sessions and validate

Restart the API to clear any warm handle to the failed metadata file:

```bash
docker compose -f compose.production.yaml restart api
docker compose -f compose.production.yaml ps
curl --fail --silent --show-error \
  "http://127.0.0.1:${LAKEHOLD_PORT:-8080}/health"
```

Before writes, verify the restored generation, latest snapshot, time travel for a known older
snapshot, per-table canary row counts and aggregates, deletion-sensitive records, and a read-only
application query. Compare against the backup manifest and the recorded recovery point. Then run a
new backup and confirm its manifest is complete.

## External dependency recovery

### PostgreSQL metadata

Use the PostgreSQL service's tested PITR or dump restore. A LakeHold catalog backup can rebuild a
portable DuckDB metadata file, but LakeHold does not automatically repoint a PostgreSQL-backed
catalog to that file. Validate maintenance leases as well as DuckLake metadata after PostgreSQL
recovery.

### Object-store data

Restore versioned objects, delete files, and their paths to a mutually consistent point before
attaching metadata. A metadata backup references data; it does not copy that data. Prevent lifecycle
rules from expiring live data, metadata backups, or eject bundles prematurely.

If metadata and object data restore to different points, keep the catalog read-only and escalate for
integrity analysis.

## Forensic fallback

If only raw Parquet and delete files survive, follow the
[verified reconstruction notes](../EXIT-PATH.md#disaster-recovery-the-metadata-catalog-is-lost) in an
isolated copy. This is not a normal production restore:

- unflushed inlined data is lost;
- snapshot history, views, comments, and file-liveness information can be lost;
- naive globbing can resurrect deleted rows and duplicate updated rows;
- superseded files can be indistinguishable from live files.

Treat the result as a new, independently validated dataset. Do not replace production state until
data owners accept the documented losses.

## Restore drills

### Monthly catalog drill

Restore one representative complete generation to a new metadata file in an isolated environment.
Verify attachment, latest and historical snapshots, deletion-sensitive rows, canary counts and
aggregates, elapsed time, and cleanup of the isolated artifact. Rotate through all critical catalogs.

### Quarterly full-node drill

Assume the primary host and volume are gone. Recover only from the off-host archive, checksum,
Compose file, pinned images, secret store, and external dependency backups. Restore to an empty host,
run every validation in the full-state procedure, test alert delivery, and record actual RPO/RTO.

A drill fails if it requires:

- a file or token available only on the lost host;
- an undocumented control-plane database edit;
- deleting or overwriting an existing recovery target;
- treating `/health` as data-integrity proof;
- restoring an incomplete manifest;
- using an unpinned or upgraded image.

Track every failed step to an owner and due date, update this runbook, and repeat the failed portion
after remediation.
