# Storage configuration and UI plan

LakeHold supports local or shared filesystems, Amazon S3 and S3-compatible services, Google Cloud
Storage, and Azure Blob Storage or ADLS Gen2 for DuckLake Parquet data. **How that is configured is
documented once, in [POSTGRES-AND-STORAGE.md](POSTGRES-AND-STORAGE.md)** — environment keys, per
provider examples, the production Compose override, and the catalog-creation contract. This document
does not repeat them; it defines the UI work required to make storage placement visible and
selectable without weakening LakeHold's credential boundary.

**Current status:** storage is configured by the deployment. The first-run Workbench creates a
catalog using the deployment's default storage root and profile. The catalog API already accepts an
explicit data path and named profile, but the Workbench does not expose those fields. There is no UI
for entering, displaying, or changing object-store credentials.

## What the UI is working with

Two separate parts, and only one of them is ever a secret:

1. A **data path** — placement. For object storage the URI carries the bucket or container and
   prefix. Safe to display, safe to accept from an administrator.
2. A **storage profile** — named credentials and protocol settings held by the deployment. A
   catalog record persists only the profile name. The credential itself must never reach a form, a
   response, a log, or the control-plane database.

The default layout is tenant-qualified — `<DataRoot>/<tenant-key>/<catalog>/` — with backup and
eject roots as siblings of the data root. The UI previews that derivation; it does not compute it.

## Product decision

The UI should make deployment-owned storage **visible and selectable**, but it must not become a
database-backed cloud-key vault.

The first useful version will therefore:

- show the configured default root and redacted profile inventory to an instance administrator;
- let the administrator select a configured profile and data path while creating a catalog;
- show the resolved, immutable placement on catalog detail screens; and
- direct credential changes to deployment configuration with provider-specific instructions.

It will not accept an S3 secret, GCS HMAC secret, or Azure connection string into a normal form and
persist it in PostgreSQL. Catalog rows continue to contain only a profile name and generated DuckDB
secret names. API responses and logs never contain the underlying credential.

## Proposed user journeys

### 1. First workspace

Extend the existing first-run card after **Catalog name** with a collapsed **Storage** section.

The default state is:

```text
Storage
Use deployment default
Resolved location: s3://company-lake/lakehold/acme/analytics/
Profile: primary (Amazon S3)
```

An **Advanced placement** choice exposes:

- storage type: Filesystem, S3, GCS, or Azure;
- a configured-profile dropdown filtered to the selected type;
- bucket/container and prefix, presented as one data-path field initially;
- a read-only final-path preview; and
- a read-only catalog toggle.

For filesystem paths, show a visible warning:

> Filesystem storage is safe for one node, or when every node mounts the same durable filesystem at
> the same path. Use object storage for ordinary multi-node deployments.

The request then passes `dataPath`, `storageProfile`, and `readOnly` through the existing catalog
creation API. The default path remains a one-click path and does not force infrastructure choices on
a first-time local user.

### 2. Add another catalog

Add **New catalog** to the instance-administration flow, using the same storage component as first
run. Do not duplicate validation or provider help text between the two forms.

The form must distinguish:

- **Use deployment default**, which previews the generated tenant-qualified path; and
- **Choose exact placement**, which sends an explicit data path and profile.

Changing the tenant or catalog name updates the derived preview before submission.

### 3. System Settings → Storage

Add a Storage card to **System Settings**, visible only to an instance-scoped administrator. It
shows deployment configuration as read-only operational state:

| Field | Display |
|---|---|
| Default data root | Full local path or object-store URI |
| Default profile | Profile name and kind |
| Backup root | Full path or URI |
| Eject root | Full path or URI |
| Profiles | Name, kind, endpoint host where applicable, region, TLS, URL style |
| Credentials | `Configured` or `Missing`; never the value, suffix, length, or hash |
| Configuration source | `Deployment environment`; restart required for changes |

Each provider gets copyable environment-variable names and a short example, linking to
[POSTGRES-AND-STORAGE.md](POSTGRES-AND-STORAGE.md) rather than restating it. The UI must say that
buckets and containers are provisioned outside LakeHold.

Do not show the S3 key ID either. Although it is less sensitive than the secret, returning it is not
needed to select a profile and makes redaction rules harder to reason about.

### 4. Catalog storage summary

The existing catalog/storage area should show:

- resolved data path;
- storage kind;
- selected profile name for remote storage;
- read-only/read-write attachment mode; and
- a clear **Placement cannot be edited in place** notice.

This is configuration metadata, not a raw bucket browser. Table files and sizes continue to come
from DuckLake metadata. The UI must not perform an unbounded bucket listing or present arbitrary
objects as catalog data.

## API work

### Redacted instance storage configuration

Add an instance-authorised read endpoint:

```http
GET /api/v1/system-settings/storage
```

It nests under the existing `/api/v1/system-settings` group rather than introducing a `/system`
sibling: that group already carries `AddEndpointFilter<LakeholdAuthorizationFilter>()` and
`RequireCapability(Capability.Instance)`, which is precisely this endpoint's authorization, and two
spellings for one instance-operator surface would be a route-naming split with nothing behind it.

Suggested response:

```json
{
  "dataRoot": "s3://company-lake/lakehold",
  "backupRoot": "s3://company-backups/lakehold",
  "ejectRoot": "s3://company-exports/lakehold",
  "defaultStorageProfile": "primary",
  "profiles": [
    {
      "name": "primary",
      "kind": "S3",
      "region": "eu-west-1",
      "endpoint": null,
      "useSsl": true,
      "urlStyle": "vhost",
      "credentialsConfigured": true
    }
  ],
  "requiresRestartToChange": true
}
```

For Azure, return whether connection-string or account/credential-chain mode is configured, but not
the connection string, account name, or chain content. The endpoint must build an explicit DTO; it
must never serialise `LakehouseOptions` or `ParquetStorageProfileOptions` directly.

`requiresRestartToChange` is a constant, and the DTO must document why rather than merely state it:
`LakehouseOptions` is bound at startup, and the profile inventory it exposes is what
`DucklingSessionConfigurator` turns into DuckDB secrets. Editing the deployment's environment
changes nothing in a running process.

### Resolve a proposed default path

Add a non-mutating resolution endpoint:

```http
POST /api/v1/system-settings/storage/resolve

{
  "tenantSlug": "acme",
  "catalogName": "analytics"
}
```

Supplying `dataPath` validates that path instead of deriving one, so the browser checks an explicit
placement through the same rules rather than re-implementing them. The response carries the resolved
path, kind, profile name, and whether the path was derived — the browser needs the last of these
to know whether editing a name will move it.

It applies the same placement rules as catalog creation, because both call one `CatalogPlacement`
helper, and creates no directory, object, metadata schema, or catalog row. Two checks deliberately
stay behind in creation: a duplicate catalog name and a data path already assigned to another
catalog. Both are create-time conflicts that only the write can settle, and the write still does.

The tenant need not exist. First run previews a placement for a workspace it is about to create, so
requiring the row would remove the preview from the one place it matters most.

A path *template* returned in the system-storage response was the considered alternative and is
rejected: it moves URI joining into Angular, where it would drift from
`CatalogStorageNamespace.Under` and from the scheme and profile-kind checks in `AdminEndpoints`. The
preview must be produced by the same code that will accept or reject the eventual create.

### Catalog creation

No contract expansion is required: `CreateCatalogRequest` already has `dataPath`, `readOnly`, and
`storageProfile`. The Angular `WorkspaceRequest` and `LakehouseService.createCatalog` currently drop
those values and must be extended.

## Validation and failure behaviour

The UI may guide, but the API remains authoritative. Required behaviour:

- Filter profiles by storage kind in the browser and validate the match again on the server.
- Normalise only for display; do not silently rewrite an operator's explicit path.
- Never create a bucket or container automatically.
- Never fall back from a failed remote profile to local disk.
- Never include credentials in validation errors, telemetry, audit details, or support copy.
- Keep the submitted form populated after a recoverable error, excluding any future one-time secret.
- Treat an unknown profile as stale deployment configuration and ask the operator to refresh.
- Make it explicit that configuration changes require an API restart in the deployment-backed
  version.

A later **Test access** action must be designed separately. It must use the configured server-side
profile, be scoped to the exact proposed prefix, have bounded time and request counts, and leave no
probe object behind. It must not require credentials to round-trip through the browser. Until those
semantics and provider-specific permissions are tested, catalog attach/create remains the real
access check.

## Delivery phases

### Phase 1 — documentation and redacted discovery — **landed**

- ~~Add the redacted `GET /api/v1/system-settings/storage` endpoint.~~ `SystemSettingsEndpoints`.
- ~~Add contract and authorization tests proving only an instance administrator can read it.~~
  Exercised over a real pipeline, not by calling the handler: the claim is about which group the
  route is mapped into, and a direct-call test stays green when that changes.
- ~~Add serialization tests proving keys, secrets, session tokens, connection strings, account
  names, and credential-chain values cannot appear.~~ Asserted against the response *bytes* using
  sentinel credentials, so a member added later — or reached through a nested type — is caught.
- ~~Add **System Settings → Storage** as a read-only card with provider-specific deployment
  help.~~ `storage-configuration.component.*`.
- ~~Document a production Compose storage override.~~
  [POSTGRES-AND-STORAGE.md](POSTGRES-AND-STORAGE.md).

**Outcome:** an operator can see what this node will use without inspecting logs or source files.

Two things were added that the phase did not ask for, both because the alternative was an untested
claim. `credentialsConfigured` mirrors the exact settings `DucklingSessionConfigurator` requires
before it will create a secret, so a profile reported as ready is one that will actually attach; and
a test binds the documented `Lakehouse__StorageProfiles__…` environment keys through the real
configuration pipeline, because documentation asserting a key that binds to nothing would leave an
operator configuring a profile this node never sees.

### Phase 2 — storage-aware catalog creation — **landed**

- ~~Introduce one reusable Angular catalog-placement form model/component.~~
  `catalog-placement.component.*`, used by both forms rather than copied into each.
- ~~Extend first-run `WorkspaceRequest` and `LakehouseService.createCatalog` to pass the existing
  API fields.~~ Omitted entirely for the default, so one-click sends the body it always did.
- ~~Add **New catalog** for instance administrators.~~ `catalog-administration.component.*`, in
  System Settings beside the storage card.
- ~~Default to deployment placement, with an advanced exact-placement option.~~
- ~~Add the server-derived path preview and filesystem/multi-node warning.~~

**Outcome:** an operator can choose among already-configured filesystems, buckets, containers, and
profiles from the UI.

Three notes on what the phase turned out to require:

- The placement rules moved into `src/Lakehold.Api/Storage/CatalogPlacement.cs` and catalog creation
  now calls it. Resolving and creating through two implementations would let a preview show one
  location and the create that follows produce another, which is worse than no preview at all.
- `CatalogStorageNamespace.Under` *throws* on a name it cannot use. Creation validates before
  reaching it, but the preview is called while an operator is still typing, so the helper guards its
  inputs — otherwise ordinary keystrokes answer 500.
- The placement component hides itself when the profile inventory cannot be read. That read is
  instance-scoped, and a caller without it can still create a catalog in the default location, which
  is exactly the behaviour that existed before the component.

### Phase 3 — catalog placement visibility — **landed**

- ~~Add the immutable storage summary to catalog details.~~ At the head of the Storage tab, above
  the physical figures: data path, kind, profile, and attachment mode.
- ~~Surface profile-not-configured and kind-mismatch errors as actionable configuration errors.~~
- ~~Link to migration documentation instead of offering an edit control.~~

**Outcome:** every catalog explains where its Parquet lives and which deployment profile grants
access.

No API work was required, and that is the finding. `CatalogDto` has carried `dataPath`,
`storageKind`, `storageProfile`, and `isReadOnly` at `Listing` capability all along; the Angular
`Catalog` model simply dropped the last two. Widening the model was the whole data change, so the
summary costs no extra request.

The error re-framing keys off the server's own wording rather than a client-side comparison. Client
detection would need the profile inventory, which is instance-scoped — so it would work for an
administrator and silently do nothing for the tenant actually blocked. The server message stays the
authority; the panel only decides whether to present it as configuration, and a message that stops
mentioning a storage profile degrades to the ordinary banner rather than to a confident wrong one.

### Phase 4 — optional secret-provider integration

Only pursue editable profiles after LakeHold has an operator-owned external secret-provider
contract for storage credentials. The UI would save a secret **reference**, never secret material,
and the API would resolve it only while creating a prefix-scoped DuckDB secret.

This phase requires its own threat model, rotation behaviour, audit events, availability semantics,
and tests proving a tenant query cannot reach another catalog's prefix. It is not required for the
first three phases.

## Test and acceptance matrix

| Area | Required evidence |
|---|---|
| Filesystem | Default and explicit paths; tenant/catalog qualification; multi-node warning |
| S3 | Default and exact path; region; session token; profile mismatch; missing bucket failure |
| S3-compatible | Endpoint, TLS, path/vhost style; MinIO integration |
| GCS | `gs://` and `gcs://`; HMAC profile; mismatch and missing credentials |
| Azure | `az://`, `azure://`, and `abfss://`; connection-string and identity modes |
| Authorization | Instance administrator allowed; tenant owner/editor/reader refused |
| Redaction | No credential field or value in responses, UI state, errors, logs, or snapshots |
| First run | Default remains one click; advanced choice reaches the existing request correctly |
| Existing catalogs | Placement displayed; no in-place edit offered |
| Regression | Full backend, frontend, PostgreSQL/S3 integration, and Workbench E2E suites pass |

## Deliberate non-goals

- Creating cloud buckets, containers, IAM users, roles, policies, or lifecycle rules.
- Browsing arbitrary objects beneath a bucket.
- Persisting raw cloud credentials in the LakeHold control plane.
- Moving an existing catalog by editing its data path.
- Treating shared object storage as distributed execution of a single DuckDB query.
- Promising that MinIO integration proves AWS IAM, Google HMAC, or Azure identity behaviour.

## Implementation pointers

- Storage configuration reference: `docs/POSTGRES-AND-STORAGE.md`
- Storage options: `src/Lakehold.Engine/Configuration/LakehouseOptions.cs`
- Profile-to-DuckDB-secret configuration: `src/Lakehold.Api/Storage/DucklingSessionConfigurator.cs`
- Catalog creation and validation: `src/Lakehold.Api/Endpoints/AdminEndpoints.cs`
- Instance-operator endpoint group: `src/Lakehold.Api/Endpoints/SystemSettingsEndpoints.cs`
- Catalog request/response contracts: `src/Lakehold.Api/Contracts.cs`
- Current first-run flow: `web/lakehold-ui/src/app/first-run.component.ts`
- Current Angular catalog call: `web/lakehold-ui/src/app/lakehouse.service.ts`
- System Settings surface: `web/lakehold-ui/src/app/system-settings.component.*`
- Production deployment: `compose.production.yaml`
- Workbench surface plan: `docs/UI.md`
