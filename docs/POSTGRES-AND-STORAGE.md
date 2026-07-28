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

### S3 or S3-compatible

```text
Lakehouse__DataRoot=s3://my-bucket/lakehold
Lakehouse__DefaultStorageProfile=primary
Lakehouse__StorageProfiles__primary__Kind=S3
Lakehouse__StorageProfiles__primary__KeyId=...
Lakehouse__StorageProfiles__primary__Secret=...
Lakehouse__StorageProfiles__primary__Region=eu-west-1
```

For MinIO or another compatible service, also set `Endpoint`, `UseSsl`, and `UrlStyle` on the
profile. `UrlStyle=path` is common for local-compatible endpoints.

### Google Cloud Storage

Set the profile `Kind` to `Gcs` and supply `KeyId`/`Secret` from a GCS interoperability HMAC key.
These are not a Google service-account JSON key.

### Azure Blob Storage or ADLS

Set `Kind=Azure` and use either:

- `AzureConnectionString` for an explicit storage connection string; or
- `AzureAccountName` plus optional `AzureCredentialChain`, such as
  `workload_identity;managed_identity`.

The Azure extension is loaded only for catalogs using an Azure profile.

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
