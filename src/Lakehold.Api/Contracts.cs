using Lakehold.Engine.Execution;
using Lakehold.Engine.Catalog;
using Lakehold.ControlPlane.Model;

namespace Lakehold.Api;

/// <summary>Request to execute a statement.</summary>
/// <param name="Sql">Backward-compatible SQL source.</param>
/// <param name="Language">Planner id. Defaults to <c>sql</c>.</param>
/// <param name="Source">Source for <paramref name="Language"/>.</param>
public sealed record ExecuteRequest(string? Sql = null, string? Language = null, string? Source = null)
{
    public string EffectiveLanguage => string.IsNullOrWhiteSpace(Language) ? "sql" : Language.Trim();

    public string? EffectiveSource => Source ?? Sql;
}

/// <summary>A column in a query response.</summary>
public sealed record ColumnDto(string Name, string DataType, string ClrType);

/// <summary>A query response.</summary>
/// <param name="Columns">Column schema in ordinal order.</param>
/// <param name="Rows">Rows aligned to <paramref name="Columns"/> by ordinal.</param>
/// <param name="Truncated">Whether the row ceiling cut the result short.</param>
/// <param name="ElapsedMilliseconds">Server-side execution time.</param>
/// <param name="RowsAffected">
///     Rows changed by a statement whose outcome is a count — <c>INSERT</c>, <c>UPDATE</c>,
///     <c>DELETE</c>, <c>MERGE</c> — and null for anything else. Null and zero differ: null means the
///     statement does not report a count, zero means a DML statement matched nothing.
/// </param>
public sealed record QueryResponse(
    IReadOnlyList<ColumnDto> Columns,
    IReadOnlyList<object?[]> Rows,
    bool Truncated,
    double ElapsedMilliseconds,
    long? RowsAffected,
    string Language,
    string? GeneratedSql,
    IReadOnlyList<Lakehold.Querying.QueryDiagnostic> Diagnostics)
{
    /// <summary>Maps the engine result onto the transport contract.</summary>
    public static QueryResponse From(
        QueryResult result,
        string language = "sql",
        string? generatedSql = null,
        IReadOnlyList<Lakehold.Querying.QueryDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new QueryResponse(
            [.. result.Columns.Select(c => new ColumnDto(c.Name, c.DataType, c.ClrType))],
            result.Rows,
            result.Truncated,
            result.Elapsed.TotalMilliseconds,
            result.RowsAffected,
            language,
            generatedSql,
            diagnostics ?? []);
    }
}

/// <summary>Metadata emitted as the first line of an NDJSON query stream.</summary>
public sealed record QueryStreamSchemaDto(string Type, IReadOnlyList<ColumnDto> Columns);

/// <summary>One row emitted by an NDJSON query stream.</summary>
public sealed record QueryStreamRowDto(string Type, IReadOnlyList<object?> Values);

/// <summary>Terminal metadata emitted after a query stream completes successfully.</summary>
public sealed record QueryStreamCompleteDto(string Type, long RowCount);

/// <summary>Terminal metadata emitted when a response fails after streaming has begun.</summary>
public sealed record StreamErrorDto(string Type, string Code, string RequestId, string Detail);

/// <summary>A reusable, catalog-scoped query definition.</summary>
/// <param name="Revision">Optimistic authoring revision.</param>
/// <param name="PublishedRevision">Revision currently exposed by the view, or null when unpublished.</param>
public sealed record SavedQueryDto(
    int Id,
    string Name,
    string? Description,
    string Sql,
    string Language,
    int Revision,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    int? CreatedByTokenId,
    int? UpdatedByTokenId,
    string? PublishedSchema,
    string? PublishedViewName,
    string? PublishedSchemaFingerprint,
    bool PublishedSchemaDrifted,
    int? PublishedRevision,
    DateTimeOffset? PublishedUtc);

/// <summary>Request to save the current source as a reusable query.</summary>
public sealed record CreateSavedQueryRequest(
    string Name,
    string? Description,
    string Sql,
    string Language = "sql");

/// <summary>Request to replace a saved-query definition at an expected revision.</summary>
public sealed record UpdateSavedQueryRequest(
    int Revision,
    string Name,
    string? Description,
    string Sql,
    string Language = "sql");

/// <summary>Request to publish one saved-query revision as a catalog view.</summary>
public sealed record PublishSavedQueryRequest(int Revision, string Schema, string ViewName);

/// <summary>A tenant, as returned by the API.</summary>
public sealed record TenantDto(string Slug, string DisplayName, IReadOnlyList<CatalogDto> Catalogs);

/// <summary>The effective access the current browser has to the workbench.</summary>
/// <param name="Mode"><c>open</c>, <c>authenticated</c>, or <c>demo</c>.</param>
/// <param name="Role">Effective tenant role, lower-case for direct display and client comparison.</param>
/// <param name="ReadOnly">Whether catalogs are attached without write access.</param>
/// <param name="SystemAdmin">Whether this credential may manage instance-wide settings.</param>
/// <param name="TenantAdmin">
///     Whether this credential may administer its own workspace's users and tokens. Decided by
///     <c>CapabilityPolicy</c> rather than left for a client to infer from <paramref name="Role"/>:
///     a read-only or catalog-narrowed owner token is least privilege by design and holds no such
///     capability, so a client deriving it from the role alone offers a surface the API refuses.
/// </param>
/// <param name="CanCreateUsers">
///     Whether this node creates identities, which only built-in identity mode does. Reported by the
///     API rather than inferred, for the same reason as <paramref name="TenantAdmin"/>: the client
///     cannot see the mode or whether a provisioning credential is present, and a form that appears
///     without both is one that fails at submit.
/// </param>
public sealed record AccessDto(
    string Mode,
    string Role,
    bool ReadOnly,
    bool SystemAdmin,
    bool TenantAdmin,
    bool CanCreateUsers = false);

/// <summary>Non-secret state used by the Workbench to offer the configured sign-in method.</summary>
/// <param name="OidcEnabled">Whether a browser sign-in flow is configured and can be offered.</param>
/// <param name="Authenticated">Whether this browser holds a signed-in session.</param>
/// <param name="DisplayName">Who is signed in, when anyone is.</param>
/// <param name="SystemAdmin">Whether that identity administers the instance.</param>
/// <param name="HasAccess">
///     Whether the signed-in identity currently reaches anything. False for a first arrival awaiting
///     approval and for a suspended member: both are authenticated and neither can do anything, and
///     telling them apart in a response would disclose which subjects this deployment knows.
/// </param>
public sealed record BrowserSessionDto(
    bool OidcEnabled,
    bool Authenticated,
    string? DisplayName,
    bool SystemAdmin,
    bool HasAccess);

/// <summary>Instance-wide settings currently effective for MCP requests.</summary>
public sealed record SystemSettingsDto(
    bool McpEnabled,
    bool McpAllowWrites,
    bool McpAllowOperatorCommands,
    int McpMaxRowsPerResult,
    string McpPublicBaseUrl,
    string McpRoute,
    int Version,
    DateTimeOffset? UpdatedUtc);

/// <summary>A complete, optimistic replacement of the mutable MCP settings.</summary>
public sealed record UpdateSystemSettingsRequest(
    bool McpEnabled,
    bool McpAllowWrites,
    int McpMaxRowsPerResult,
    string? McpPublicBaseUrl,
    int Version,
    bool? McpAllowOperatorCommands = null);

/// <summary>
///     Where this node places Parquet data, and which storage profiles it can authenticate with.
/// </summary>
/// <remarks>
///     Deliberately an explicit projection rather than a serialised <c>LakehouseOptions</c>. Every
///     credential-bearing setting — <c>KeyId</c>, <c>Secret</c>, <c>SessionToken</c>,
///     <c>AzureConnectionString</c>, <c>AzureAccountName</c>, and <c>AzureCredentialChain</c> — lives
///     on the options type this stands in front of, so a shape that mirrored it would publish them
///     the moment one was added. Invariant 8: an object-store credential never reaches a response.
/// </remarks>
/// <param name="RequiresRestartToChange">
///     Always true, and not a placeholder for a future toggle. <c>LakehouseOptions</c> is bound
///     once at startup and its profiles are what <c>DucklingSessionConfigurator</c> turns into DuckDB
///     secrets, so editing the deployment's environment changes nothing in a running process. The UI
///     needs this to say "restart required" rather than imply an unsaved edit.
/// </param>
public sealed record SystemStorageDto(
    string DataRoot,
    string BackupRoot,
    string EjectRoot,
    string? DefaultStorageProfile,
    IReadOnlyList<StorageProfileSummaryDto> Profiles,
    bool RequiresRestartToChange);

/// <summary>One configured storage profile, reduced to what selecting it requires.</summary>
/// <param name="Kind">
///     <c>Local</c>, <c>S3</c>, <c>Gcs</c>, or <c>Azure</c>. The UI filters profiles by this so a
///     bucket URI cannot be paired with a profile that could not authenticate against it.
/// </param>
/// <param name="Endpoint">
///     Host and port of an S3-compatible service, or null for the provider's own endpoint. Any
///     userinfo is stripped before this is returned: <c>ENDPOINT</c> takes a bare host, but a
///     deployment that puts credentials there anyway must not have them reflected back.
/// </param>
/// <param name="CredentialsConfigured">
///     Whether the settings this profile's kind requires are present — mirroring what
///     <c>DucklingSessionConfigurator</c> insists on when it creates the secret, so a profile that
///     reads as configured here is one that will actually attach. Never the value, and never its
///     length, suffix, or hash: those narrow a secret without ever being needed to select a profile.
/// </param>
/// <param name="AzureAuthentication">
///     <c>connection-string</c> or <c>credential-chain</c> for an Azure profile, null otherwise. Which
///     mode is in use is operational fact; the connection string, account name, and chain content are
///     not returned in any form.
/// </param>
public sealed record StorageProfileSummaryDto(
    string Name,
    string Kind,
    string? Region,
    string? Endpoint,
    bool UseSsl,
    string UrlStyle,
    bool CredentialsConfigured,
    string? AzureAuthentication);

/// <summary>Asks where a catalog's Parquet would go, without creating anything.</summary>
/// <param name="TenantSlug">
///     The workspace the catalog would belong to. It need not exist yet: the first-run form previews
///     a placement for a tenant it is about to create, so requiring the row would make the preview
///     useless exactly where it is most wanted.
/// </param>
/// <param name="DataPath">
///     Null or empty to derive the tenant-qualified path beneath the deployment's data root. A value
///     is validated instead of derived, which is how the browser checks an explicit placement without
///     re-implementing scheme and profile-kind rules.
/// </param>
public sealed record ResolveStoragePathRequest(
    string TenantSlug,
    string CatalogName,
    string? DataPath = null,
    string? StorageProfile = null);

/// <summary>A validated placement. Nothing was created, and nothing is reserved by asking.</summary>
/// <param name="Derived">
///     True when the path came from the deployment's roots. The browser needs this to know whether
///     editing the tenant or catalog name will move the path it is showing.
/// </param>
public sealed record ResolvedStoragePathDto(
    string DataPath,
    string Kind,
    string? StorageProfile,
    bool Derived);

/// <summary>A column created by a tabular-file import.</summary>
public sealed record TabularImportedColumnDto(string Name, string DataType);

/// <summary>One rejected CSV line returned to the browser.</summary>
public sealed record CsvRejectDto(
    long Line,
    string? ColumnName,
    string ErrorType,
    string CsvLine,
    string ErrorMessage);

/// <summary>The created table and bounded reject report for one browser CSV or XLSX import.</summary>
public sealed record TabularImportDto(
    string FileName,
    string Format,
    string Schema,
    string Table,
    long RowsImported,
    long RejectedRows,
    long RecordedErrors,
    bool RejectsTruncated,
    bool UsedAutomaticFallback,
    IReadOnlyList<TabularImportedColumnDto> Columns,
    IReadOnlyList<CsvRejectDto> Rejects,
    double ElapsedMilliseconds)
{
    /// <summary>Maps the engine result to the browser contract.</summary>
    public static TabularImportDto From(TabularImportResult result)
        => new(
            result.FileName,
            result.Format.ToString().ToLowerInvariant(),
            result.Schema,
            result.Table,
            result.RowsImported,
            result.RejectedRows,
            result.RecordedErrors,
            result.RejectsTruncated,
            result.UsedAutomaticFallback,
            [.. result.Columns.Select(column => new TabularImportedColumnDto(column.Name, column.DataType))],
            [.. result.Rejects.Select(reject => new CsvRejectDto(
                reject.Line,
                reject.ColumnName,
                reject.ErrorType,
                reject.CsvLine,
                reject.ErrorMessage))],
            result.Elapsed.TotalMilliseconds);
}

/// <summary>A managed source and its governed target data product.</summary>
public sealed record DataConnectorDto(
    int Id,
    string Name,
    string? Description,
    string Owner,
    IReadOnlyList<string> Tags,
    string Kind,
    string AdapterId,
    int AdapterVersion,
    string ReadMode,
    string EndpointUrl,
    string? CredentialEnvironmentVariable,
    string RestResponseFormat,
    DataConnectorSourceSettingsDto SourceSettings,
    DataConnectorAuthenticationDto Authentication,
    IReadOnlyList<DataConnectorFieldMappingDto> FieldMappings,
    string SchemaPolicy,
    IReadOnlyList<string> KeyColumns,
    string? Checkpoint,
    long CheckpointVersion,
    string TargetSchema,
    string TargetTable,
    long MinimumRows,
    IReadOnlyList<string> RequiredColumns,
    IReadOnlyList<string> NotNullColumns,
    bool Enabled,
    int? RefreshIntervalSeconds,
    DateTimeOffset? NextRunUtc,
    DateTimeOffset? LastCompletedUtc,
    string? LastError,
    DateTimeOffset? SourceAcknowledgementPendingUtc,
    string? SourceAcknowledgementError,
    int ConsecutiveFailures,
    int MaxAttempts,
    int RetryBaseSeconds,
    int RetryMaxSeconds,
    DateTimeOffset? PausedUtc,
    bool TargetProvisioned,
    DateTimeOffset? ArchivedUtc,
    int Version,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    public static DataConnectorDto From(DataConnector connector) => new(
        connector.Id,
        connector.Name,
        connector.Description,
        connector.Owner,
        connector.Tags(),
        connector.Kind.ToString().ToLowerInvariant(),
        connector.AdapterId,
        connector.AdapterVersion,
        connector.ReadMode == DataConnectorReadMode.Incremental ? "incremental" : "full-snapshot",
        connector.EndpointUrl,
        connector.CredentialEnvironmentVariable,
        connector.RestResponseFormat == Lakehold.ControlPlane.Model.RestResponseFormat.NewlineDelimitedJson
            ? "ndjson"
            : "json-array",
        DataConnectorSourceSettingsDto.From(connector.SourceSettings()),
        DataConnectorAuthenticationDto.From(connector.Authentication()),
        connector.FieldMappings().Select(DataConnectorFieldMappingDto.From).ToArray(),
        connector.SchemaPolicy.ToString().ToLowerInvariant(),
        connector.KeyColumns(),
        connector.Checkpoint,
        connector.CheckpointVersion,
        connector.TargetSchema,
        connector.TargetTable,
        connector.MinimumRows,
        connector.RequiredColumns(),
        connector.NotNullColumns(),
        connector.Enabled,
        connector.RefreshIntervalSeconds,
        connector.NextRunUtc,
        connector.LastCompletedUtc,
        connector.LastError,
        connector.SourceAcknowledgementPendingUtc,
        connector.SourceAcknowledgementError,
        connector.ConsecutiveFailures,
        connector.MaxAttempts,
        connector.RetryBaseSeconds,
        connector.RetryMaxSeconds,
        connector.PausedUtc,
        connector.TargetProvisioned,
        connector.ArchivedUtc,
        connector.ConcurrencyVersion,
        connector.CreatedUtc,
        connector.UpdatedUtc);
}

public sealed record DataConnectorSourceSettingsDto(
    string? SourceTable,
    string? CursorColumn,
    string? CursorType,
    int PageSize,
    IReadOnlyList<string> Properties,
    bool CursorIsCommitMonotonic,
    string? KafkaBootstrapServers,
    string? KafkaTopic,
    string? KafkaConsumerGroup,
    string? SchemaRegistryUrl)
{
    public static DataConnectorSourceSettingsDto From(DataConnectorSourceSettings settings) => new(
        settings.SourceTable,
        settings.CursorColumn,
        settings.CursorType,
        settings.PageSize,
        settings.Properties ?? [],
        settings.CursorIsCommitMonotonic,
        settings.KafkaBootstrapServers,
        settings.KafkaTopic,
        settings.KafkaConsumerGroup,
        settings.SchemaRegistryUrl);
}

public sealed record DataConnectorAuthenticationDto(
    string Kind,
    string? SecretReference,
    string? UsernameSecretReference,
    string? PasswordSecretReference,
    string? ClientIdSecretReference,
    string? ClientSecretReference,
    string? RefreshTokenSecretReference,
    string? ClientCertificateSecretReference,
    string? CertificatePasswordSecretReference,
    string? CustomHeaderName,
    string? SchemaRegistryUsernameSecretReference,
    string? SchemaRegistryPasswordSecretReference)
{
    public static DataConnectorAuthenticationDto From(DataConnectorAuthentication authentication) => new(
        authentication.Kind switch
        {
            DataConnectorAuthenticationKind.None => "none",
            DataConnectorAuthenticationKind.Bearer => "bearer",
            DataConnectorAuthenticationKind.OAuthRefreshToken => "oauth-refresh-token",
            DataConnectorAuthenticationKind.MutualTls => "mtls",
            DataConnectorAuthenticationKind.CustomHeader => "custom-header",
            DataConnectorAuthenticationKind.PostgreSqlPassword => "postgresql-password",
            DataConnectorAuthenticationKind.KafkaSaslPlain => "kafka-sasl-plain",
            _ => throw new ArgumentOutOfRangeException(nameof(authentication)),
        },
        authentication.SecretReference,
        authentication.UsernameSecretReference,
        authentication.PasswordSecretReference,
        authentication.ClientIdSecretReference,
        authentication.ClientSecretReference,
        authentication.RefreshTokenSecretReference,
        authentication.ClientCertificateSecretReference,
        authentication.CertificatePasswordSecretReference,
        authentication.CustomHeaderName,
        authentication.SchemaRegistryUsernameSecretReference,
        authentication.SchemaRegistryPasswordSecretReference);
}

public sealed record DataConnectorFieldMappingDto(string Source, string Target, string Transform)
{
    public static DataConnectorFieldMappingDto From(DataConnectorFieldMapping mapping) => new(
        mapping.Source,
        mapping.Target,
        mapping.Transform switch
        {
            DataConnectorTransformKind.None => "none",
            DataConnectorTransformKind.Trim => "trim",
            DataConnectorTransformKind.Lowercase => "lowercase",
            DataConnectorTransformKind.Uppercase => "uppercase",
            DataConnectorTransformKind.ToString => "to-string",
            _ => throw new ArgumentOutOfRangeException(nameof(mapping)),
        });
}

/// <summary>Create or replace the mutable definition of a managed connector.</summary>
public sealed record DataConnectorDefinitionRequest(
    string Name,
    string? Description,
    string Owner,
    IReadOnlyList<string>? Tags,
    string Kind,
    string EndpointUrl,
    string? CredentialEnvironmentVariable,
    string? RestResponseFormat,
    string TargetSchema,
    string TargetTable,
    long MinimumRows = 1,
    IReadOnlyList<string>? RequiredColumns = null,
    IReadOnlyList<string>? NotNullColumns = null,
    bool Enabled = false,
    int? RefreshIntervalSeconds = null,
    string? AdapterId = null,
    int AdapterVersion = 1,
    string? ReadMode = null,
    string? SchemaPolicy = null,
    IReadOnlyList<string>? KeyColumns = null,
    IReadOnlyList<DataConnectorFieldMappingRequest>? FieldMappings = null,
    DataConnectorSourceSettingsRequest? SourceSettings = null,
    DataConnectorAuthenticationRequest? Authentication = null,
    int MaxAttempts = 5,
    int RetryBaseSeconds = 30,
    int RetryMaxSeconds = 3_600);

/// <summary>Validated, non-secret source configuration accepted by connector adapters.</summary>
public sealed record DataConnectorSourceSettingsRequest(
    string? SourceTable = null,
    string? CursorColumn = null,
    string? CursorType = null,
    int PageSize = 100,
    IReadOnlyList<string>? Properties = null,
    bool CursorIsCommitMonotonic = false,
    string? KafkaBootstrapServers = null,
    string? KafkaTopic = null,
    string? KafkaConsumerGroup = null,
    string? SchemaRegistryUrl = null);

/// <summary>Approved authentication mode and secret references; secret values are never accepted.</summary>
public sealed record DataConnectorAuthenticationRequest(
    string Kind = "none",
    string? SecretReference = null,
    string? UsernameSecretReference = null,
    string? PasswordSecretReference = null,
    string? ClientIdSecretReference = null,
    string? ClientSecretReference = null,
    string? RefreshTokenSecretReference = null,
    string? ClientCertificateSecretReference = null,
    string? CertificatePasswordSecretReference = null,
    string? CustomHeaderName = null,
    string? SchemaRegistryUsernameSecretReference = null,
    string? SchemaRegistryPasswordSecretReference = null);

/// <summary>Declarative top-level field rename and bounded transformation.</summary>
public sealed record DataConnectorFieldMappingRequest(string Source, string Target, string Transform = "none");

/// <summary>Optimistic replacement of a connector definition.</summary>
public sealed record UpdateDataConnectorRequest(int Version, DataConnectorDefinitionRequest Definition);

/// <summary>Durable lineage and quality evidence for one connector refresh.</summary>
public sealed record DataConnectorRunDto(
    int Id,
    string Trigger,
    string Status,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    long RowsRead,
    long RowsPublished,
    bool? QualityPassed,
    string? SourceVersion,
    string? InputCheckpoint,
    string? ProposedCheckpoint,
    string? ReplayKey,
    string? Error)
{
    public static DataConnectorRunDto From(DataConnectorRun run) => new(
        run.Id,
        run.Trigger.ToString().ToLowerInvariant(),
        run.Status switch
        {
            DataConnectorRunStatus.PublishedAwaitingSourceAcknowledgement =>
                "published-source-acknowledgement-pending",
            _ => run.Status.ToString().ToLowerInvariant(),
        },
        run.StartedUtc,
        run.CompletedUtc,
        run.RowsRead,
        run.RowsPublished,
        run.QualityPassed,
        run.SourceVersion,
        run.InputCheckpoint,
        run.ProposedCheckpoint,
        run.ReplayKey,
        run.Error);
}

/// <summary>Optimistic connector lifecycle operation.</summary>
public sealed record DataConnectorOperationRequest(int Version);

/// <summary>Immediate result of a manually requested connector refresh.</summary>
public sealed record DataConnectorExecutionDto(
    int RunId,
    string Status,
    long RowsRead,
    long RowsPublished,
    string? SourceVersion,
    string? Error);

/// <summary>Request to provision a tenant. Instance scope.</summary>
/// <param name="Slug">URL-safe key. Reserved value <c>admin</c> is refused — it collides with the instance-token prefix.</param>
public sealed record CreateTenantRequest(string Slug, string DisplayName);

/// <summary>Request to provision a catalog under a tenant. Instance scope.</summary>
/// <param name="Name">Bare SQL identifier; it reaches <c>ATTACH</c>, which cannot be parameterised.</param>
/// <param name="DataPath">
///     Root for Parquet data. Null derives a local path under the node's data root; a value may be a
///     local path or an object-store URI (<c>s3://</c>, <c>gs://</c>, <c>az://</c>).
/// </param>
/// <param name="ReadOnly">Attach the catalog without write access.</param>
public sealed record CreateCatalogRequest(
    string Name,
    string? DataPath = null,
    bool ReadOnly = false,
    string? StorageProfile = null);

/// <summary>Request to mint a tenant-scoped API token. Returned once at creation.</summary>
/// <param name="Name">Human-facing label. Not a secret and not an identifier.</param>
/// <param name="ReadOnly">Whether the token produces a read-only catalog attachment.</param>
/// <param name="CatalogName">Optional least-privilege narrowing to one catalog in the tenant.</param>
/// <param name="ExpiresUtc">Optional expiry; a token past this instant is refused.</param>
/// <param name="Role">
///     <c>owner</c>, <c>editor</c>, or <c>reader</c>. New tokens default to <c>reader</c>. Tokens
///     persisted before roles existed retain their historical owner capability. A <c>reader</c> is
///     read-only regardless of <paramref name="ReadOnly"/>.
/// </param>
public sealed record CreateTokenRequest(
    string Name,
    bool ReadOnly = false,
    string? CatalogName = null,
    DateTimeOffset? ExpiresUtc = null,
    string? Role = null);

/// <summary>A freshly minted token. The <see cref="Token"/> is shown once and is never recoverable.</summary>
public sealed record CreatedTokenDto(int Id, string Name, string Token);

/// <summary>Token metadata, as listed by the API. Never carries the secret.</summary>
public sealed record ApiTokenDto(
    int Id,
    string Name,
    string Scope,
    string Role,
    string? CatalogName,
    bool ReadOnly,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? ExpiresUtc,
    DateTimeOffset? RevokedUtc,
    DateTimeOffset? LastUsedUtc);

/// <summary>A person's membership of a workspace, as returned by the API.</summary>
/// <param name="Subject">
///     The identity provider's stable identifier. Shown so an administrator approving a stranger can
///     match them against the provider; it is not a secret, and it is the only thing that reliably
///     identifies someone when display names collide.
/// </param>
public sealed record TenantMemberDto(
    int Id,
    string Subject,
    string? DisplayName,
    string? Email,
    string Role,
    string Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastSeenUtc);

/// <summary>Changes what a membership grants. Absent fields are left as they are.</summary>
/// <remarks>
///     Both parameters carry defaults so the exported contract marks them optional. A positional
///     record parameter without one is emitted as <c>required</c>, which would oblige a caller
///     changing only a role to send an explicit null status.
/// </remarks>
public sealed record UpdateTenantMemberRequest(string? Role = null, string? Status = null);

/// <summary>Creates an identity in the provider and a membership for it in one step.</summary>
/// <remarks>
///     Only available in built-in identity mode. Under SSO the people already exist and Lakehold has
///     no business writing to somebody else's directory.
/// </remarks>
public sealed record CreateTenantMemberRequest(
    string Username,
    string? Email = null,
    string? DisplayName = null,
    string? Role = null);

/// <summary>A newly created member, and the one-time password if the provider did not email one.</summary>
/// <param name="TemporaryPassword">
///     Returned exactly once, in this response, and stored nowhere. Null where the provider was asked
///     to send its own invitation instead. The same handling as a freshly minted API token: the
///     administrator copies it now or issues a new one.
/// </param>
public sealed record CreatedTenantMemberDto(TenantMemberDto Member, string? TemporaryPassword);

/// <summary>A catalog, as returned by the API.</summary>
/// <remarks>
///     Deliberately omits <c>MetadataSource</c> and <c>StorageSecretName</c>. The former can be a
///     PostgreSQL connection string and the latter names a credential; neither belongs in a
///     response the browser receives.
/// </remarks>
public sealed record CatalogDto(
    string Name,
    string DataPath,
    bool IsReadOnly,
    string MetadataKind,
    string StorageKind,
    string? StorageProfile);

/// <summary>A column in the schema explorer.</summary>
public sealed record SchemaColumnDto(string Name, string DataType, bool IsNullable);

/// <summary>A table in the schema explorer.</summary>
public sealed record SchemaTableDto(string Name, string Kind, IReadOnlyList<SchemaColumnDto> Columns);

/// <summary>A schema in the schema explorer.</summary>
public sealed record SchemaDto(string Name, IReadOnlyList<SchemaTableDto> Tables);

/// <summary>A catalog snapshot.</summary>
public sealed record SnapshotDto(long SnapshotId, DateTimeOffset CommittedAt, long SchemaVersion, string? CommitMessage);

/// <summary>Stable keyset page of catalog snapshots.</summary>

/// <summary>Request to plan or apply a table-data restore from a snapshot.</summary>
/// <param name="Table">Base table whose rows should return to the selected snapshot.</param>
/// <param name="Schema">Schema containing <paramref name="Table"/>.</param>
/// <param name="Apply">False to return a read-only plan; true to commit the reviewed restore.</param>
/// <param name="ExpectedCurrentSnapshotId">
///     Current snapshot returned by the reviewed plan. Required for apply so an intervening write
///     cannot silently invalidate the operator's row and schema review.
/// </param>
public sealed record RestoreTableRequest(
    string Table,
    string Schema = "main",
    bool Apply = false,
    long? ExpectedCurrentSnapshotId = null);

/// <summary>A read-only plan or committed table-data restore.</summary>
public sealed record TableRestoreDto(
    string Schema,
    string Table,
    long SnapshotId,
    long CurrentSnapshotId,
    long CurrentRowCount,
    long HistoricalRowCount,
    IReadOnlyList<string> RestoredColumns,
    IReadOnlyList<string> CurrentOnlyColumns,
    IReadOnlyList<string> HistoricalOnlyColumns,
    bool DryRun);

/// <summary>One table's physical footprint in the storage view.</summary>
/// <param name="RowCount">
///     Live rows, as <c>SELECT count(*)</c> would report: merge-on-read deletes subtracted, rows
///     still inlined in the metadata catalog included.
/// </param>
/// <param name="InlinedRows">
///     Rows committed but not yet written to Parquet. The only thing distinguishing a table whose
///     data is entirely inlined from an empty one — both report zero files.
/// </param>
/// <param name="AverageFileSizeBytes">Mean bytes per data file, or null when there are no files.</param>
/// <param name="NeedsFlush">
///     Whether <c>flush</c> has work to do. Advisory, and the reason the Flush button is no longer
///     a guess.
/// </param>
/// <param name="NeedsCompaction">
///     Whether the table has drifted into the small-file problem — more than one file, averaging
///     below the catalog's <c>target_file_size</c> or the deployment's advisory floor. Advisory only.
/// </param>
public sealed record TableStorageDto(
    string SchemaName,
    string TableName,
    long RowCount,
    long InlinedRows,
    long FileCount,
    long FileSizeBytes,
    long DeleteFileCount,
    long DeleteFileSizeBytes,
    long? AverageFileSizeBytes,
    bool NeedsFlush,
    bool NeedsCompaction);

/// <summary>A catalog's storage footprint.</summary>
/// <param name="TargetFileSizeBytes">
///     The catalog's configured <c>target_file_size</c>, or null when it has never been set. Null is
///     reported rather than guessed — DuckLake's built-in default is not exposed anywhere — and
///     <paramref name="AdvisoryFileSizeBytes"/> is what the advisory actually used.
/// </param>
/// <param name="AdvisoryFileSizeBytes">
///     The threshold <c>NeedsCompaction</c> was computed against, so a caller can see the basis of
///     the advice rather than having to trust it.
/// </param>
public sealed record CatalogStorageDto(
    IReadOnlyList<TableStorageDto> Tables,
    long? TargetFileSizeBytes,
    long AdvisoryFileSizeBytes);

/// <summary>One Parquet data file in the table-detail panel.</summary>
/// <param name="DeleteFile">
///     The merge-on-read delete file paired to this data file, or null when it has none.
/// </param>
public sealed record DataFileDto(
    string DataFile,
    long DataFileSizeBytes,
    string? DeleteFile,
    long? DeleteFileSizeBytes);

/// <summary>A table's data files at one snapshot.</summary>
/// <param name="SnapshotId">The snapshot read, or null for the current one.</param>
/// <param name="Truncated">Whether the list stops short of the table's real file count.</param>
public sealed record TableFilesDto(
    string SchemaName,
    string TableName,
    long? SnapshotId,
    bool Truncated,
    IReadOnlyList<DataFileDto> Files);

/// <summary>A logical column in table detail.</summary>
public sealed record TableDetailColumnDto(string Name, string DataType, bool IsNullable);

/// <summary>One key in a DuckLake partition specification.</summary>
public sealed record PartitionKeyDto(int Position, string ColumnName, string Transform);

/// <summary>A partition specification and the snapshot interval in which it applies.</summary>
public sealed record PartitionSpecDto(
    long PartitionId,
    long BeginSnapshot,
    long? EndSnapshot,
    IReadOnlyList<PartitionKeyDto> Keys);

/// <summary>One table or view's logical, physical, and partition detail.</summary>
public sealed record TableDetailDto(
    string SchemaName,
    string TableName,
    string Kind,
    IReadOnlyList<TableDetailColumnDto> Columns,
    TableStorageDto? Storage,
    IReadOnlyList<PartitionSpecDto> PartitionSpecs,
    long? TargetFileSizeBytes,
    long AdvisoryFileSizeBytes);

/// <summary>Live summary statistics for one logical column.</summary>
public sealed record ColumnProfileDto(
    string Name,
    string DataType,
    long RowCount,
    long NullCount,
    string? Minimum,
    string? Maximum,
    string? ApproxDistinct,
    string? Mean,
    string? StandardDeviation,
    string? FirstQuartile,
    string? Median,
    string? ThirdQuartile);

/// <summary>All columns in one table or view, profiled at one snapshot.</summary>
public sealed record TableProfileDto(
    string SchemaName,
    string TableName,
    long? SnapshotId,
    long RowCount,
    IReadOnlyList<ColumnProfileDto> Columns);

/// <summary>One bounded frequency or range bucket.</summary>
public sealed record DistributionBucketDto(
    string Label,
    string? LowerBound,
    string? UpperBound,
    long Count);

/// <summary>A bounded distribution for one column.</summary>
public sealed record ColumnDistributionDto(
    string SchemaName,
    string TableName,
    string ColumnName,
    string DataType,
    long? SnapshotId,
    string Kind,
    long NullCount,
    bool Truncated,
    IReadOnlyList<DistributionBucketDto> Buckets);

/// <summary>Outcome of a maintenance operation.</summary>
/// <param name="DryRun">
///     True when the operation only reported what it would do. Destructive operations default to
///     this; the caller must pass <c>?apply=true</c> to commit.
/// </param>
public sealed record MaintenanceDto(string Operation, string Detail, double ElapsedMilliseconds, bool DryRun);

/// <summary>An entry in the query history panel.</summary>
/// <param name="TokenId">The credential that ran the statement, or null for pre-auth history.</param>
/// <param name="TokenName">
///     The label of that credential when it still exists, for a readable audit trail; null when the
///     run was anonymous or the token has since been deleted.
/// </param>
/// <param name="MemberId">The tenant member who ran the statement, when it was a person.</param>
/// <param name="ActorKind">Whether the actor was an API token, member, system process, or unknown.</param>
/// <param name="ActorName">Best-effort current display label for the token or member.</param>
/// <param name="Origin">Transport or subsystem through which the statement entered LakeHold.</param>
public sealed record QueryRunDto(
    int Id,
    string CatalogName,
    string Sql,
    string Language,
    DateTimeOffset StartedUtc,
    double ElapsedMilliseconds,
    int RowCount,
    bool Succeeded,
    string? Error,
    int? TokenId,
    string? TokenName,
    int? MemberId,
    string ActorKind,
    string? ActorName,
    string Origin);

/// <summary>A backup generation available to restore.</summary>
/// <param name="Complete">
///     False when the generation has no manifest — it died partway through and restoring it could
///     silently omit deletions.
/// </param>
public sealed record BackupGenerationDto(
    string Generation,
    DateTimeOffset? CreatedUtc,
    long? SnapshotId,
    int TableCount,
    bool Complete);

/// <summary>Request to rebuild a catalog from a backup.</summary>
/// <param name="Generation">Generation to restore, or null for the newest complete one.</param>
/// <param name="TargetMetadataPath">
///     Where to write the rebuilt catalog. Must not already exist — restore never overwrites.
/// </param>
public sealed record RestoreRequest(string? Generation, string TargetMetadataPath);

/// <summary>Outcome of a restore.</summary>
public sealed record RestoreResponse(string MetadataPath, string Generation, int TablesRestored, long RowsRestored);

/// <summary>A recent scheduled maintenance run.</summary>
public sealed record ScheduledRunDto(
    string Job,
    string Tenant,
    string Catalog,
    DateTimeOffset StartedUtc,
    double ElapsedMilliseconds,
    bool Succeeded,
    string Detail);

/// <summary>Request to write a verified eject bundle.</summary>
/// <param name="IncludeHistory">
///     Whether to also copy the metadata catalog so snapshots and time travel survive the export.
///     The data half is reader-agnostic without it; history requires the catalog.
/// </param>
public sealed record EjectRequest(bool IncludeHistory = false);

/// <summary>Outcome of an eject.</summary>
/// <param name="Verified">
///     True when every table's independent re-read matched the catalog's row count. Always true on
///     success — a mismatch fails the request instead.
/// </param>
/// <param name="DigestDeferred">
///     True when per-file digests were skipped because the bundle is on an object store.
/// </param>
public sealed record EjectResponse(
    string Location,
    int TableCount,
    long TotalRows,
    bool Verified,
    bool DigestDeferred,
    bool IsSigned,
    bool IncludesHistory);

/// <summary>An attested table inside an eject bundle.</summary>
public sealed record EjectedTableDto(
    string Schema,
    string Table,
    long RowCount,
    string? Sha256,
    long? Bytes);

/// <summary>An eject bundle available on disk.</summary>
/// <param name="Complete">False when the bundle has no manifest — it died partway and is untrusted.</param>
public sealed record EjectBundleDto(
    string Bundle,
    DateTimeOffset? CreatedUtc,
    long? SnapshotId,
    bool IncludesHistory,
    bool IsSigned,
    bool Complete,
    IReadOnlyList<EjectedTableDto> Tables);

/// <summary>A page of row-level changes from the pull CDC surface.</summary>
public sealed record ChangePageDto(
    string Schema,
    string Table,
    long FromSnapshot,
    long ToSnapshot,
    bool Truncated,
    IReadOnlyList<ChangeDto> Changes,
    string? NextCursor = null);

/// <summary>One row-level change.</summary>
/// <param name="ChangeType">
///     <c>insert</c>, <c>delete</c>, <c>update_preimage</c>, or <c>update_postimage</c>.
/// </param>
public sealed record ChangeDto(
    long SnapshotId,
    long RowId,
    string ChangeType,
    IReadOnlyDictionary<string, object?> Row);

/// <summary>Metadata emitted as the first line of an NDJSON CDC stream.</summary>
public sealed record ChangeStreamStartDto(
    string Type,
    string Schema,
    string Table,
    long FromSnapshot,
    long ToSnapshot);

/// <summary>One change emitted by an NDJSON CDC stream.</summary>
public sealed record ChangeStreamItemDto(string Type, ChangeDto Change);

/// <summary>Terminal metadata emitted after a CDC stream completes successfully.</summary>
public sealed record ChangeStreamCompleteDto(string Type, long ChangeCount, long ToSnapshot);

/// <summary>Request to create a change subscription.</summary>
/// <param name="EndpointUrl">HTTP or HTTPS endpoint the signed payloads are posted to.</param>
/// <param name="Secret">
///     Shared secret used to HMAC-sign every delivery. Write-only: it is never returned by any
///     endpoint after creation.
/// </param>
/// <param name="Table">Table to watch, or null to watch every base table in the catalog.</param>
/// <param name="Schema">Schema of <paramref name="Table"/>. Defaults to <c>main</c>.</param>
public sealed record CreateSubscriptionRequest(
    string EndpointUrl,
    string Secret,
    string? Table = null,
    string Schema = "main");

/// <summary>Mutable webhook controls. Omitted fields retain their current value.</summary>
public sealed record UpdateSubscriptionRequest(
    bool? Active = null,
    string? Secret = null,
    long? ReplayFromSnapshot = null,
    bool RetryNow = false);

/// <summary>A change subscription, as returned by the API.</summary>
/// <remarks>
///     Deliberately omits the signing secret: it is write-only. Delivery state is included because a
///     subscription you cannot observe is a subscription you do not trust.
/// </remarks>
public sealed record SubscriptionDto(
    int Id,
    string Catalog,
    string Schema,
    string? Table,
    string EndpointUrl,
    bool Active,
    long LastDeliveredSnapshot,
    int ConsecutiveFailures,
    DateTimeOffset? LastAttemptUtc,
    string? LastError,
    DateTimeOffset CreatedUtc);

/// <summary>Registers or resumes one durable pull consumer at its committed target checkpoint.</summary>
public sealed record RegisterCdcConsumerRequest(string Name, long LastAppliedSnapshot);

/// <summary>Advances a durable pull consumer after its target transaction commits.</summary>
public sealed record AdvanceCdcConsumerRequest(long LastAppliedSnapshot);

/// <summary>Observable retention checkpoint for a durable pull consumer.</summary>
public sealed record CdcConsumerDto(
    int Id,
    string Name,
    string Catalog,
    long LastAppliedSnapshot,
    bool Active,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
