# PostgreSQL and Parquet storage

LakeHold has two durable database responsibilities and one compute responsibility:

- `ConnectionStrings:ControlPlane` is PostgreSQL for tenants, catalog definitions, token hashes,
  subscriptions, schedules, and audit history.
- `ConnectionStrings:DuckLakeMetadata` is PostgreSQL for DuckLake metadata. LakeHold creates one
  deterministic schema per tenant catalog.
- DuckDB remains in-process compute inside each API/worker node. PostgreSQL is not the table-data
  query engine, and LakeHold does not turn one query into distributed SQL.

The two connection strings may point at one database in development. Production should use
separate users, and may use separate databases, so control-plane migrations and DuckLake catalog
writes have independently rotatable credentials.

Startup fails closed if `ConnectionStrings:ControlPlane` is absent. EF migrations run automatically
under a PostgreSQL advisory lock, making concurrent node startup safe. Creating or opening a new
catalog also requires `ConnectionStrings:DuckLakeMetadata`; that user needs permission to create and
manage the catalog's schema.

## Parquet locations

Every DuckLake table is stored as Parquet under the catalog's `DataPath`.

| Data path | Profile kind | Position |
|---|---|---|
| `/data/catalog` or `file://...` | `Local` | Supported for one node, or a filesystem genuinely shared by every worker |
| `s3://bucket/prefix` | `S3` | Recommended for multi-node; Amazon S3 or S3-compatible endpoints |
| `gs://bucket/prefix` or `gcs://...` | `Gcs` | Recommended for multi-node; uses GCS interoperability HMAC keys |
| `az://...`, `azure://...`, `abfss://...` | `Azure` | Recommended for multi-node; Blob Storage or ADLS Gen2 |

Catalog rows persist only a profile name and generated DuckDB secret names. Credentials remain in
the deployment configuration/secret store. PostgreSQL and DuckLake profile secrets exist only while
DuckLake attaches; LakeHold drops them before tenant SQL can run, and recreates the PostgreSQL
credential only inside a gated trusted metadata operation. Object-storage secrets remain available
for table access, but each is scoped to the tenant catalog's exact data, backup, or eject prefix.

Default durable locations include the tenant key:

```text
<DataRoot>/<tenant-key>/<catalog>/
<BackupRoot>/<tenant-key>/<catalog>/<UTC generation>/
<EjectRoot>/<tenant-key>/<catalog>/<UTC bundle>/
```

The same layout is used for local paths and object-store URIs. Two tenants can therefore use the
same catalog name without sharing Parquet files, backups, eject bundles, or a warm compute session.

`BackupRoot` and `EjectRoot` are deliberately siblings of `DataRoot`, never children of it. DuckLake
orphan cleanup removes unreferenced files beneath the data path, which would make a nested backup or
eject bundle delete itself once it ages past the retention cutoff.

## Where storage configuration lives

Storage has two separate parts, and the split decides where each belongs:

1. A **data path** says where a catalog's Parquet data goes. For object storage the URI carries the
   bucket or container and prefix. It is placement, not a secret.
2. A **storage profile** supplies credentials and protocol settings. Catalog records persist only
   the profile *name*; the credential stays in deployment configuration or its secret store.

For source-based development, copy `.env.example` to `.env` in the repository root. The API loads
that file before the host is built, without overwriting real environment variables:

```bash
cp .env.example .env
```

Placement settings are not secrets, so they belong in `appsettings*.json` under the `Lakehouse`
section or in a Compose override — not in `.env`. Credentials belong in `.env` for development and
in the platform's secret store for production. Never commit a credential to `appsettings*.json` or
to `.env.example`.

`Lakehouse__StateRoot` anchors the four roots. A root left at its default resolves beneath it, so
`Lakehouse__StateRoot=/var/lib/lakehold` puts the unchanged data root at `/var/lib/lakehold/data`; a
relative override resolves under the state root too, and an absolute path or a `://` URI is taken as
written. That keeps a default from following the process's working directory.

For production, supply these settings through the deployment platform's environment and secret
store. `compose.production.yaml` deliberately embeds no object store, and its API service passes an
explicit list of environment values — so a setting added only to the Compose `.env` file does
**not** reach the container. Use a Compose override, a Kubernetes Secret/ConfigMap, or the hosting
platform's equivalent.

## Filesystem

The default data root is `./.lakehold/data`, resolved against the state root as described above.
Local paths need no storage profile. To choose explicit mounts:

```dotenv
Lakehouse__DataRoot=/mnt/lakehold/data
Lakehouse__BackupRoot=/mnt/lakehold/backups
Lakehouse__EjectRoot=/mnt/lakehold/ejects
```

A filesystem is appropriate for a single API node. A multi-node deployment may use one only when
every worker sees the same durable filesystem at the same path; a node-local volume is not shared
storage.

## Amazon S3

```dotenv
Lakehouse__DataRoot=s3://my-bucket/lakehold
Lakehouse__DefaultStorageProfile=primary
Lakehouse__StorageProfiles__primary__Kind=S3
Lakehouse__StorageProfiles__primary__KeyId=change-me
Lakehouse__StorageProfiles__primary__Secret=change-me
Lakehouse__StorageProfiles__primary__Region=eu-west-1
```

Temporary session credentials may also supply:

```dotenv
Lakehouse__StorageProfiles__primary__SessionToken=change-me
```

The bucket must already exist. LakeHold does not create production buckets.

## MinIO and other S3-compatible services

Use the S3 profile kind and add the endpoint-specific settings:

```dotenv
Lakehouse__DataRoot=s3://my-bucket/lakehold
Lakehouse__DefaultStorageProfile=primary
Lakehouse__StorageProfiles__primary__Kind=S3
Lakehouse__StorageProfiles__primary__KeyId=change-me
Lakehouse__StorageProfiles__primary__Secret=change-me
Lakehouse__StorageProfiles__primary__Endpoint=minio.example.com:9000
Lakehouse__StorageProfiles__primary__UseSsl=false
Lakehouse__StorageProfiles__primary__UrlStyle=path
```

`UrlStyle=path` is common for compatible endpoints. Production should use TLS unless the endpoint is
on an explicitly trusted private network. The development Compose stack creates only its
`lakehold-test` integration-test bucket; that is test fixture setup, not application catalog
provisioning.

## Google Cloud Storage

```dotenv
Lakehouse__DataRoot=gs://my-bucket/lakehold
Lakehouse__DefaultStorageProfile=primary
Lakehouse__StorageProfiles__primary__Kind=Gcs
Lakehouse__StorageProfiles__primary__KeyId=gcs-hmac-access-id
Lakehouse__StorageProfiles__primary__Secret=gcs-hmac-secret
```

`gcs://` is accepted as well as `gs://`. DuckDB's GCS path uses interoperability HMAC credentials:
`KeyId` and `Secret` are not a Google service-account JSON document.

## Azure Blob Storage and ADLS Gen2

Connection-string authentication:

```dotenv
Lakehouse__DataRoot=az://my-container/lakehold
Lakehouse__DefaultStorageProfile=primary
Lakehouse__StorageProfiles__primary__Kind=Azure
Lakehouse__StorageProfiles__primary__AzureConnectionString=change-me
```

Account name with a workload or managed identity chain:

```dotenv
Lakehouse__DataRoot=az://my-container/lakehold
Lakehouse__DefaultStorageProfile=primary
Lakehouse__StorageProfiles__primary__Kind=Azure
Lakehouse__StorageProfiles__primary__AzureAccountName=mystorageaccount
Lakehouse__StorageProfiles__primary__AzureCredentialChain=workload_identity;managed_identity
```

`azure://` and `abfss://` data paths are recognised as well as `az://`. The container or filesystem
must already exist. The Azure extension is loaded only for catalogs using an Azure profile.

## Production Compose example

Keep non-secret placement settings in an override and resolve secrets from the deployment's secret
manager, or from `.env` in a development-grade deployment:

```yaml
# compose.storage.yaml
services:
  api:
    environment:
      Lakehouse__DataRoot: s3://company-lake/lakehold
      Lakehouse__BackupRoot: s3://company-backups/lakehold
      Lakehouse__EjectRoot: s3://company-exports/lakehold
      Lakehouse__DefaultStorageProfile: primary
      Lakehouse__StorageProfiles__primary__Kind: S3
      Lakehouse__StorageProfiles__primary__KeyId: ${LAKEHOLD_S3_KEY}
      Lakehouse__StorageProfiles__primary__Secret: ${LAKEHOLD_S3_SECRET}
      Lakehouse__StorageProfiles__primary__Region: eu-west-1
```

Start the deployment with both files:

```bash
docker compose -f compose.production.yaml -f compose.storage.yaml up -d
```

The override is an example, not a reason to commit credentials. A production secret manager should
inject `LAKEHOLD_S3_KEY` and `LAKEHOLD_S3_SECRET`.

## Multiple buckets and profiles

A profile is named credentials and connection settings; the bucket stays part of each catalog's data
path. A deployment can define more than one:

```dotenv
Lakehouse__StorageProfiles__primary__Kind=S3
Lakehouse__StorageProfiles__primary__KeyId=...
Lakehouse__StorageProfiles__primary__Secret=...
Lakehouse__StorageProfiles__primary__Region=eu-west-1

Lakehouse__StorageProfiles__archive__Kind=S3
Lakehouse__StorageProfiles__archive__KeyId=...
Lakehouse__StorageProfiles__archive__Secret=...
Lakehouse__StorageProfiles__archive__Region=eu-central-1
```

## Selecting placement when creating a catalog

An instance administrator can name an exact path and profile through the catalog API:

```http
POST /api/v1/tenants/acme/catalogs
Authorization: Bearer <instance credential>
Content-Type: application/json

{
  "name": "analytics",
  "dataPath": "s3://customer-bucket/lakehold/acme/analytics",
  "storageProfile": "primary",
  "readOnly": false
}
```

Omitting `dataPath` derives it from `DataRoot`, the tenant slug, and the catalog name. Omitting
`storageProfile` for a remote path uses `DefaultStorageProfile`. LakeHold rejects:

- an unsupported URI scheme;
- a remote path without a configured profile;
- a profile whose kind does not match the URI;
- a local path paired with an object-storage profile; and
- a data path already assigned to another catalog.

Placement is chosen when the catalog is created. There is no in-place catalog storage update
endpoint: moving an existing catalog is a migration, not a settings toggle, and must preserve
DuckLake metadata, inlined rows, deletes, updates, and history.

Surfacing this configuration in the Workbench is planned in
[STORAGE-CONFIGURATION-AND-UI-PLAN.md](STORAGE-CONFIGURATION-AND-UI-PLAN.md).

## Importing a legacy DuckDB control plane

The importer is explicit and dry-run-first. Stop legacy writers before either pass. It acquires a
DuckDB read-only lock, refuses an outstanding `.wal`, copies the checkpointed source to a temporary
location, adapts that disposable copy to the current legacy schema, and never writes the original.

```bash
dotnet run --project src/Lakehold.ControlPlane.Import -- \
  --source /path/to/controlplane.duckdb \
  --target "$LAKEHOLD_CONTROL_PLANE"
```

Review the inventory, keep all writers to the legacy deployment stopped, then apply:

```bash
dotnet run --project src/Lakehold.ControlPlane.Import -- \
  --source /path/to/controlplane.duckdb \
  --target "$LAKEHOLD_CONTROL_PLANE" \
  --apply
```

The target is migrated first and must contain no application rows. The import is transactional,
preserves IDs and token hashes, resets PostgreSQL identity sequences, and refuses a non-empty
target. Imported catalogs keep their existing metadata/data locations; moving those catalogs to
PostgreSQL DuckLake metadata is a separate data-plane migration.

If the importer reports an outstanding WAL, do not delete it. Open the source with DuckDB after all
writers are stopped and run `CHECKPOINT`, then rerun the dry run. The refusal prevents committed
rows that exist only in the WAL from being silently omitted.
