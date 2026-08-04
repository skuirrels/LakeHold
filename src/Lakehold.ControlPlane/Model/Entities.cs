using Lakehold.Engine.Catalog;
using System.Text.Json;

namespace Lakehold.ControlPlane.Model;

/// <summary>
///     The instance-wide operator settings shared by every API node.
/// </summary>
/// <remarks>
///     This is intentionally a singleton row. Settings that must take effect without a process
///     restart belong in the shared control plane, not in node-local configuration or an in-memory
///     options cache.
/// </remarks>
public sealed class SystemSettings
{
    /// <summary>Maximum length of the externally reachable MCP base URL.</summary>
    public const int McpPublicBaseUrlMaxLength = 2048;

    /// <summary>The fixed singleton key.</summary>
    public int Id { get; set; } = 1;

    public bool McpEnabled { get; set; }

    public bool McpAllowWrites { get; set; }

    public int McpMaxRowsPerResult { get; set; }

    public string? McpPublicBaseUrl { get; set; }

    /// <summary>Optimistic version used to reject two operators overwriting one another.</summary>
    public int ConcurrencyVersion { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>
///     Durable response ledger for one public-API idempotency key. The key itself is never stored;
///     only its SHA-256 digest is persisted.
/// </summary>
/// <remarks>
///     An interrupted request remains <see cref="ApiIdempotencyStatus.InProgress"/> and is never
///     reclaimed automatically. That fail-closed choice can require operator intervention, but it
///     cannot silently execute an indeterminate mutation twice.
/// </remarks>
public sealed class ApiIdempotencyRecord
{
    public int Id { get; set; }

    public required string Scope { get; set; }

    public required string KeyHash { get; set; }

    public required string RequestHash { get; set; }

    public ApiIdempotencyStatus Status { get; set; }

    public int? ResponseStatusCode { get; set; }

    public string? ResponseContentType { get; set; }

    public string? ResponseLocation { get; set; }

    public byte[]? ResponseBody { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? CompletedUtc { get; set; }

    public long ConcurrencyVersion { get; set; }

    public void Complete(
        int statusCode,
        string? contentType,
        string? location,
        byte[] responseBody,
        DateTimeOffset completedUtc)
    {
        if (Status != ApiIdempotencyStatus.InProgress)
        {
            throw new InvalidOperationException("Only an in-progress idempotency record can complete.");
        }

        ArgumentNullException.ThrowIfNull(responseBody);
        ResponseStatusCode = statusCode;
        ResponseContentType = contentType;
        ResponseLocation = location;
        ResponseBody = responseBody;
        CompletedUtc = completedUtc;
        Status = ApiIdempotencyStatus.Completed;
        ConcurrencyVersion++;
    }
}

public enum ApiIdempotencyStatus
{
    InProgress = 0,
    Completed = 1,
}

/// <summary>Durable state for one long-running public API operation.</summary>
public sealed class ApiOperation
{
    public required string Id { get; set; }

    public required string TenantSlug { get; set; }

    public required string CatalogName { get; set; }

    public required string Kind { get; set; }

    public required string RequestJson { get; set; }

    public ApiOperationStatus Status { get; set; }

    public int? RequestedByTokenId { get; set; }

    public string? ResultJson { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? StartedUtc { get; set; }

    public DateTimeOffset? CompletedUtc { get; set; }

    public string? LeaseOwner { get; set; }

    public DateTimeOffset? LeaseExpiresUtc { get; set; }

    public long ConcurrencyVersion { get; set; }
}

public enum ApiOperationStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Indeterminate = 4,
}

/// <summary>
///     An isolation boundary: an organisation, team, or environment. A tenant owns catalogs, and
///     a query always executes in exactly one tenant's context.
/// </summary>
public sealed class Tenant
{
    public int Id { get; set; }

    /// <summary>Stable URL-safe key used in API routes.</summary>
    public required string Slug { get; set; }

    public required string DisplayName { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public ICollection<LakeCatalog> Catalogs { get; } = [];
}

/// <summary>
///     A DuckLake catalog belonging to a tenant. This is the control-plane record; the engine
///     turns it into a <see cref="CatalogDescriptor"/> when it attaches a compute session.
/// </summary>
public sealed class LakeCatalog
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    /// <summary>
    ///     The name the catalog is attached as, and what tenants write in SQL. Constrained to a
    ///     bare SQL identifier because it reaches <c>ATTACH</c>, which cannot be parameterised.
    /// </summary>
    public required string Name { get; set; }

    public CatalogMetadataKind MetadataKind { get; set; } = CatalogMetadataKind.LocalFile;

    /// <summary>
    ///     Local metadata path for a legacy single-node catalog, or the name of the temporary
    ///     DuckLake profile secret created when a PostgreSQL-backed catalog is attached.
    /// </summary>
    public required string MetadataSource { get; set; }

    /// <summary>PostgreSQL schema containing this catalog's DuckLake metadata tables.</summary>
    public string? MetadataSchema { get; set; }

    /// <summary>Name of the temporary DuckDB PostgreSQL credential secret.</summary>
    public string? MetadataSecretName { get; set; }

    /// <summary>Root URI for Parquet data files. Local path, or <c>s3://</c>, <c>gs://</c>, <c>az://</c>.</summary>
    public required string DataPath { get; set; }

    /// <summary>
    ///     Name of a DuckDB secret granting access to <see cref="DataPath"/>, created during session
    ///     start-up. Only the name is persisted; the credential itself never reaches this table.
    /// </summary>
    public string? StorageSecretName { get; set; }

    /// <summary>Backend holding the catalog's Parquet data files.</summary>
    public ParquetStorageKind StorageKind { get; set; } = ParquetStorageKind.Local;

    /// <summary>
    ///     Deployment configuration profile used to resolve storage credentials. The profile name
    ///     is not a credential and may safely be persisted.
    /// </summary>
    public string? StorageProfile { get; set; }

    /// <summary>
    ///     Monotonic version of attach-affecting configuration. It forms part of each node's warm
    ///     session key, so a changed record cannot reuse an attachment made from older settings.
    /// </summary>
    public long ConfigurationVersion { get; set; } = 1;

    public bool IsReadOnly { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public Tenant Tenant { get; set; } = null!;

    public ICollection<SavedQuery> SavedQueries { get; } = [];

    public ICollection<DataConnector> DataConnectors { get; } = [];

    /// <summary>Projects this record into the descriptor the engine attaches.</summary>
    public CatalogDescriptor ToDescriptor() => new(
        Name,
        MetadataKind,
        MetadataSource,
        DataPath,
        StorageSecretName,
        IsReadOnly,
        MetadataSchema: MetadataSchema,
        MetadataSecretName: MetadataSecretName,
        TenantKey: Tenant.Slug,
        CatalogId: Id,
        ConfigurationVersion: ConfigurationVersion,
        StorageKind: StorageKind,
        StorageProfile: StorageProfile);
}

/// <summary>The transport used to read a full dataset snapshot from an external source.</summary>
public enum DataConnectorKind
{
    Rest = 0,
    Grpc = 1,
    PostgreSql = 2,
    HubSpot = 3,
}

/// <summary>Whether an adapter publishes a complete replacement or a checkpointed keyed delta.</summary>
public enum DataConnectorReadMode
{
    FullSnapshot = 0,
    Incremental = 1,
}

/// <summary>How publication handles a source schema that differs from the managed target.</summary>
public enum DataConnectorSchemaPolicy
{
    Reject = 0,
    Additive = 1,
    MappedVersion = 2,
}

/// <summary>Approved authentication mechanisms understood by the connector runtime.</summary>
public enum DataConnectorAuthenticationKind
{
    None = 0,
    Bearer = 1,
    OAuthRefreshToken = 2,
    MutualTls = 3,
    CustomHeader = 4,
    PostgreSqlPassword = 5,
}

/// <summary>Bounded, declarative transformations; connector definitions never execute user code.</summary>
public enum DataConnectorTransformKind
{
    None = 0,
    Trim = 1,
    Lowercase = 2,
    Uppercase = 3,
    ToString = 4,
}

/// <summary>The wire shape returned by a REST connector.</summary>
public enum RestResponseFormat
{
    JsonArray = 0,
    NewlineDelimitedJson = 1,
}

/// <summary>Why a connector refresh was started.</summary>
public enum DataConnectorTrigger
{
    Manual = 0,
    Scheduled = 1,
}

/// <summary>The durable outcome of one connector refresh.</summary>
public enum DataConnectorRunStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
    DeadLettered = 3,
}

/// <summary>
///     A managed full-snapshot ingestion definition. It is both the source-to-table lineage edge and
///     the first data-product metadata record: the target has an owner, description, tags, and
///     explicit quality gates rather than being an anonymous table produced by a background task.
/// </summary>
public sealed class DataConnector
{
    private DataConnector()
    {
    }

    public int Id { get; private set; }

    public int TenantId { get; private set; }

    public int CatalogId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public string Owner { get; private set; } = string.Empty;

    /// <summary>JSON array of discovery tags. Stored as text for the legacy DuckDB test adapter.</summary>
    public string TagsJson { get; private set; } = "[]";

    public DataConnectorKind Kind { get; private set; }

    public string AdapterId { get; private set; } = "lakehold.rest";

    public int AdapterVersion { get; private set; } = 1;

    public DataConnectorReadMode ReadMode { get; private set; }

    public string EndpointUrl { get; private set; } = string.Empty;

    /// <summary>
    ///     Name of an environment variable containing a bearer token. The secret itself is never
    ///     persisted and must be supplied consistently to every worker node.
    /// </summary>
    public string? CredentialEnvironmentVariable { get; private set; }

    public RestResponseFormat RestResponseFormat { get; private set; }

    /// <summary>Validated non-secret adapter configuration.</summary>
    public string SourceSettingsJson { get; private set; } = "{}";

    /// <summary>Authentication mechanism and secret references only; never secret values.</summary>
    public string AuthenticationJson { get; private set; } = "{}";

    /// <summary>Validated field mappings and bounded transformation names.</summary>
    public string FieldMappingsJson { get; private set; } = "[]";

    public DataConnectorSchemaPolicy SchemaPolicy { get; private set; }

    /// <summary>Columns that make incremental upsert replay idempotent.</summary>
    public string KeyColumnsJson { get; private set; } = "[]";

    /// <summary>Opaque adapter cursor committed only after DuckLake publication succeeds.</summary>
    public string? Checkpoint { get; private set; }

    public long CheckpointVersion { get; private set; }

    public string TargetSchema { get; private set; } = "main";

    public string TargetTable { get; private set; } = string.Empty;

    public long MinimumRows { get; private set; } = 1;

    /// <summary>JSON array of columns that must exist before a refresh may replace the target.</summary>
    public string RequiredColumnsJson { get; private set; } = "[]";

    /// <summary>JSON array of columns that must contain no nulls.</summary>
    public string NotNullColumnsJson { get; private set; } = "[]";

    public bool Enabled { get; private set; }

    /// <summary>Null means manual-only; otherwise the interval between successful or failed attempts.</summary>
    public int? RefreshIntervalSeconds { get; private set; }

    public DateTimeOffset? NextRunUtc { get; private set; }

    public DateTimeOffset? LastCompletedUtc { get; private set; }

    public string? LastError { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    public int MaxAttempts { get; private set; } = 5;

    public int RetryBaseSeconds { get; private set; } = 30;

    public int RetryMaxSeconds { get; private set; } = 3_600;

    public DateTimeOffset? PausedUtc { get; private set; }

    public string? LeaseOwner { get; private set; }

    public DateTimeOffset? LeaseExpiresUtc { get; private set; }

    /// <summary>Opaque generation that fences an expired worker from a later claim.</summary>
    public string? LeaseToken { get; private set; }

    /// <summary>True after this connector has safely created its target and may replace it.</summary>
    public bool TargetProvisioned { get; private set; }

    /// <summary>Archived definitions and their run lineage remain durable and cannot execute.</summary>
    public DateTimeOffset? ArchivedUtc { get; private set; }

    public int ConcurrencyVersion { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }

    public DateTimeOffset UpdatedUtc { get; private set; }

    public Tenant Tenant { get; private set; } = null!;

    public LakeCatalog Catalog { get; private set; } = null!;

    public ICollection<DataConnectorRun> Runs { get; } = [];

    public static DataConnector Create(
        int tenantId,
        int catalogId,
        DataConnectorDefinition definition,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var connector = new DataConnector
        {
            TenantId = tenantId,
            CatalogId = catalogId,
            CreatedUtc = now,
            ConcurrencyVersion = 1,
        };
        connector.Apply(definition, now);
        return connector;
    }

    public void Reconfigure(DataConnectorDefinition definition, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (ArchivedUtc is not null)
        {
            throw new InvalidOperationException("Archived connectors cannot be reconfigured.");
        }

        if (LeaseExpiresUtc > now)
        {
            throw new InvalidOperationException("A connector cannot be reconfigured while a refresh is active.");
        }

        if (TargetProvisioned
            && (!string.Equals(TargetSchema, definition.TargetSchema.Trim(), StringComparison.Ordinal)
                || !string.Equals(TargetTable, definition.TargetTable.Trim(), StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "A connector target cannot change after its first successful publication; create a new connector instead.");
        }

        if (TargetProvisioned && definition.Platform is { } platform
            && (!string.Equals(AdapterId, platform.AdapterId, StringComparison.Ordinal)
                || AdapterVersion != platform.AdapterVersion
                || ReadMode != platform.ReadMode
                || !KeyColumns().SequenceEqual(platform.KeyColumns, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "A connector adapter, read mode, or incremental key cannot change after publication; create a new connector instead.");
        }

        if (Checkpoint is not null && definition.Platform is { } checkpointed
            && !string.Equals(SourceSettingsJson, JsonSerializer.Serialize(checkpointed.SourceSettings), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A checkpointed connector's source cursor configuration cannot change; create a new connector instead.");
        }

        Apply(definition, now);
        ConcurrencyVersion++;
    }

    public string[] Tags() => DeserializeArray(TagsJson);

    public string[] RequiredColumns() => DeserializeArray(RequiredColumnsJson);

    public string[] NotNullColumns() => DeserializeArray(NotNullColumnsJson);

    public string[] KeyColumns() => DeserializeArray(KeyColumnsJson);

    public DataConnectorSourceSettings SourceSettings() =>
        JsonSerializer.Deserialize<DataConnectorSourceSettings>(SourceSettingsJson) ?? new();

    public DataConnectorAuthentication Authentication() =>
        JsonSerializer.Deserialize<DataConnectorAuthentication>(AuthenticationJson) ?? new();

    public DataConnectorFieldMapping[] FieldMappings() =>
        JsonSerializer.Deserialize<DataConnectorFieldMapping[]>(FieldMappingsJson) ?? [];

    public void MarkSucceeded(
        string leaseToken,
        DateTimeOffset now,
        bool targetPublished,
        string? proposedCheckpoint)
    {
        if (!string.Equals(LeaseToken, leaseToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The connector claim is no longer current.");
        }

        LastCompletedUtc = now;
        LastError = null;
        ConsecutiveFailures = 0;
        LeaseOwner = null;
        LeaseExpiresUtc = null;
        LeaseToken = null;
        TargetProvisioned |= targetPublished;
        if (ReadMode == DataConnectorReadMode.Incremental
            && proposedCheckpoint is not null
            && !string.Equals(Checkpoint, proposedCheckpoint, StringComparison.Ordinal))
        {
            Checkpoint = NormalizeOptional(proposedCheckpoint, 4_000);
            CheckpointVersion++;
        }
        NextRunUtc = Enabled && RefreshIntervalSeconds is { } seconds
            ? now.AddSeconds(seconds)
            : null;
        UpdatedUtc = now;
        ConcurrencyVersion++;
    }

    public bool MarkFailed(string leaseToken, DateTimeOffset now, string error)
    {
        if (!string.Equals(LeaseToken, leaseToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The connector claim is no longer current.");
        }

        LastCompletedUtc = now;
        LastError = TruncateOptional(error, 4_000);
        ConsecutiveFailures++;
        LeaseOwner = null;
        LeaseExpiresUtc = null;
        LeaseToken = null;
        var deadLettered = ConsecutiveFailures >= MaxAttempts;
        if (deadLettered)
        {
            PausedUtc = now;
            NextRunUtc = null;
        }
        else
        {
            var exponent = Math.Min(ConsecutiveFailures - 1, 30);
            var delay = Math.Min(RetryMaxSeconds, RetryBaseSeconds * Math.Pow(2, exponent));
            NextRunUtc = now.AddSeconds(delay);
        }

        UpdatedUtc = now;
        ConcurrencyVersion++;
        return deadLettered;
    }

    public void Pause(DateTimeOffset now)
    {
        if (ArchivedUtc is not null)
        {
            throw new InvalidOperationException("Archived connectors cannot be paused.");
        }

        PausedUtc ??= now;
        NextRunUtc = null;
        UpdatedUtc = now;
        ConcurrencyVersion++;
    }

    public void Resume(DateTimeOffset now, bool resetFailures)
    {
        if (ArchivedUtc is not null)
        {
            throw new InvalidOperationException("Archived connectors cannot be resumed.");
        }

        PausedUtc = null;
        if (resetFailures)
        {
            ConsecutiveFailures = 0;
            LastError = null;
        }

        NextRunUtc = Enabled && RefreshIntervalSeconds is not null ? now : null;
        UpdatedUtc = now;
        ConcurrencyVersion++;
    }

    public void Archive(DateTimeOffset now)
    {
        if (ArchivedUtc is not null)
        {
            return;
        }

        ArchivedUtc = now;
        Enabled = false;
        PausedUtc = now;
        NextRunUtc = null;
        UpdatedUtc = now;
        ConcurrencyVersion++;
    }

    private void Apply(DataConnectorDefinition definition, DateTimeOffset now)
    {
        var scheduleChanged = Enabled != definition.Enabled
                              || RefreshIntervalSeconds != definition.RefreshIntervalSeconds;
        if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Trim().Length > 200)
        {
            throw new ArgumentException("Connector names must contain 1 to 200 characters.", nameof(definition));
        }

        if (!Uri.TryCreate(definition.EndpointUrl, UriKind.Absolute, out var endpoint))
        {
            throw new ArgumentException("A connector endpoint must be an absolute URL.", nameof(definition));
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new ArgumentException("Connector endpoints must not contain embedded credentials.", nameof(definition));
        }

        if (string.IsNullOrWhiteSpace(definition.Owner) || definition.Owner.Trim().Length > 200)
        {
            throw new ArgumentException("A data-product owner of 1 to 200 characters is required.", nameof(definition));
        }

        if (definition.MinimumRows < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(definition), "Minimum rows must be at least one.");
        }


        if (!Enum.IsDefined(definition.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(definition), "Connector kind is not supported.");
        }

        var platform = definition.Platform ?? DataConnectorPlatformDefinition.Legacy(definition);
        if (platform.KeyColumns is null
            || platform.FieldMappings is null
            || platform.SourceSettings is null
            || platform.Authentication is null)
        {
            throw new ArgumentException("Connector platform fields cannot be null.", nameof(definition));
        }
        if (!Enum.IsDefined(platform.ReadMode) || !Enum.IsDefined(platform.SchemaPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(definition), "Connector read mode or schema policy is invalid.");
        }

        if (platform.AdapterVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(definition), "Adapter version must be positive.");
        }

        if (platform.ReadMode == DataConnectorReadMode.Incremental && platform.KeyColumns.Count == 0)
        {
            throw new ArgumentException("Incremental connectors require at least one key column.", nameof(definition));
        }

        if (platform.SchemaPolicy == DataConnectorSchemaPolicy.MappedVersion
            && platform.FieldMappings.Count == 0)
        {
            throw new ArgumentException("Mapped-version schema policy requires at least one field mapping.", nameof(definition));
        }

        if (platform.SchemaPolicy != DataConnectorSchemaPolicy.MappedVersion
            && platform.FieldMappings.Count > 0)
        {
            throw new ArgumentException("Field mappings require mapped-version schema policy.", nameof(definition));
        }

        if (platform.MaxAttempts is < 1 or > 100
            || platform.RetryBaseSeconds is < 1 or > 86_400
            || platform.RetryMaxSeconds < platform.RetryBaseSeconds
            || platform.RetryMaxSeconds > 604_800)
        {
            throw new ArgumentOutOfRangeException(nameof(definition), "Connector retry policy is invalid.");
        }

        ValidatePlatform(definition.Kind, endpoint, platform);

        if (definition.Enabled && definition.RefreshIntervalSeconds is null)
        {
            throw new ArgumentException(
                "Enabled connectors require a refresh interval.",
                nameof(definition));
        }

        if (definition.RefreshIntervalSeconds is < 60 or > 31_536_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(definition), "Refresh intervals must be between 60 seconds and 365 days.");
        }

        var credentialVariable = NormalizeOptional(definition.CredentialEnvironmentVariable, 128);
        if (credentialVariable is not null && !IsEnvironmentVariableName(credentialVariable))
        {
            throw new ArgumentException(
                "Credential environment-variable names must start with a letter or underscore and contain only letters, digits, and underscores.",
                nameof(definition));
        }

        Name = definition.Name.Trim();
        Description = NormalizeOptional(definition.Description, 2_000);
        Owner = definition.Owner.Trim();
        TagsJson = SerializeArray(definition.Tags, 64, 100);
        Kind = definition.Kind;
        AdapterId = Required(platform.AdapterId, 128, "Adapter id");
        AdapterVersion = platform.AdapterVersion;
        ReadMode = platform.ReadMode;
        EndpointUrl = definition.EndpointUrl.Trim();
        CredentialEnvironmentVariable = credentialVariable;
        RestResponseFormat = definition.RestResponseFormat;
        SourceSettingsJson = SerializeObject(platform.SourceSettings, 32_768);
        AuthenticationJson = SerializeObject(platform.Authentication, 16_384);
        FieldMappingsJson = SerializeMappings(platform.FieldMappings);
        SchemaPolicy = platform.SchemaPolicy;
        KeyColumnsJson = SerializeArray(platform.KeyColumns, 64, 255);
        TargetSchema = Required(definition.TargetSchema, 63, "Target schema");
        TargetTable = Required(definition.TargetTable, 63, "Target table");
        MinimumRows = definition.MinimumRows;
        RequiredColumnsJson = SerializeArray(definition.RequiredColumns, 256, 255);
        NotNullColumnsJson = SerializeArray(definition.NotNullColumns, 256, 255);
        Enabled = definition.Enabled;
        MaxAttempts = platform.MaxAttempts;
        RetryBaseSeconds = platform.RetryBaseSeconds;
        RetryMaxSeconds = platform.RetryMaxSeconds;
        RefreshIntervalSeconds = definition.RefreshIntervalSeconds;
        NextRunUtc = definition.Enabled && definition.RefreshIntervalSeconds is not null
            ? scheduleChanged || NextRunUtc is null ? now : NextRunUtc
            : null;
        UpdatedUtc = now;
    }

    private static string Required(string value, int maxLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength)
        {
            throw new ArgumentException($"{field} must contain 1 to {maxLength} characters.");
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"Values cannot exceed {maxLength} characters.");
        }

        return normalized;
    }

    private static string? TruncateOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized[..Math.Min(normalized.Length, maxLength)];
    }

    private static bool IsEnvironmentVariableName(string value)
    {
        if (!(char.IsAsciiLetter(value[0]) || value[0] == '_'))
        {
            return false;
        }

        return value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }

    private static string SerializeArray(IEnumerable<string> values, int maxCount, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(values);
        var normalized = values
            .Select(value => value?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length > maxCount || normalized.Any(value => value.Length > maxLength))
        {
            throw new ArgumentException(
                $"Lists may contain at most {maxCount} values of at most {maxLength} characters each.");
        }

        return JsonSerializer.Serialize(normalized);
    }

    private static string[] DeserializeArray(string json) =>
        JsonSerializer.Deserialize<string[]>(json) ?? [];

    private static string SerializeObject<T>(T value, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        var json = JsonSerializer.Serialize(value);
        if (json.Length > maxLength)
        {
            throw new ArgumentException($"Connector configuration cannot exceed {maxLength} characters.");
        }

        return json;
    }

    private static string SerializeMappings(IReadOnlyList<DataConnectorFieldMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        if (mappings.Count > 256)
        {
            throw new ArgumentException("A connector may declare at most 256 field mappings.");
        }

        var normalized = mappings.Select(mapping => mapping is null
                ? throw new ArgumentException("Field mappings cannot contain null entries.", nameof(mappings))
                : new DataConnectorFieldMapping(
                    Required(mapping.Source, 255, "Mapping source"),
                    Required(mapping.Target, 255, "Mapping target"),
                    mapping.Transform))
            .ToArray();
        foreach (var mapping in normalized)
        {
            if (!Enum.IsDefined(mapping.Transform))
            {
                throw new ArgumentOutOfRangeException(nameof(mappings), "A mapping transform is invalid.");
            }
        }

        if (normalized.Select(mapping => mapping.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != normalized.Length
            || normalized.Select(mapping => mapping.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != normalized.Length)
        {
            throw new ArgumentException("Field mapping source and target names must be unique.");
        }

        return SerializeObject(normalized, 65_536);
    }

    private static void ValidatePlatform(
        DataConnectorKind kind,
        Uri endpoint,
        DataConnectorPlatformDefinition platform)
    {
        _ = Required(platform.AdapterId, 128, "Adapter id");

        if (kind == DataConnectorKind.PostgreSql
            && (!(endpoint.Scheme is "postgresql" or "postgres")
                || string.IsNullOrWhiteSpace(endpoint.AbsolutePath.Trim('/'))))
        {
            throw new ArgumentException("PostgreSQL connector endpoints must use postgresql://host/database.");
        }

        if (kind is DataConnectorKind.Rest or DataConnectorKind.Grpc
            && endpoint.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException("REST and gRPC connector endpoints must use http or https.");
        }

        if (kind == DataConnectorKind.HubSpot
            && (endpoint.Scheme != Uri.UriSchemeHttps
                || !string.Equals(endpoint.DnsSafeHost, "api.hubapi.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("HubSpot connector endpoints must use https://api.hubapi.com.");
        }

        if (!Enum.IsDefined(platform.Authentication.Kind))
        {
            throw new ArgumentException("The connector authentication kind is invalid.");
        }

        var authentication = platform.Authentication;
        var references = new[]
        {
            authentication.SecretReference,
            authentication.UsernameSecretReference,
            authentication.PasswordSecretReference,
            authentication.ClientIdSecretReference,
            authentication.ClientSecretReference,
            authentication.RefreshTokenSecretReference,
            authentication.ClientCertificateSecretReference,
            authentication.CertificatePasswordSecretReference,
        };
        if (references.Where(value => value is not null).Any(value =>
                !(value!.StartsWith("env://", StringComparison.OrdinalIgnoreCase)
                  || value.StartsWith("vault://", StringComparison.OrdinalIgnoreCase))))
        {
            throw new ArgumentException("Connector credentials must be external secret references.");
        }

        var missingAuthentication = authentication.Kind switch
        {
            DataConnectorAuthenticationKind.Bearer or DataConnectorAuthenticationKind.CustomHeader =>
                authentication.SecretReference is null,
            DataConnectorAuthenticationKind.MutualTls => authentication.ClientCertificateSecretReference is null,
            DataConnectorAuthenticationKind.OAuthRefreshToken => authentication.ClientIdSecretReference is null
                                                                 || authentication.ClientSecretReference is null
                                                                 || authentication.RefreshTokenSecretReference is null,
            DataConnectorAuthenticationKind.PostgreSqlPassword => authentication.UsernameSecretReference is null
                                                                   || authentication.PasswordSecretReference is null,
            _ => false,
        };
        if (missingAuthentication)
        {
            throw new ArgumentException("Connector authentication is missing required secret references.");
        }

        if (authentication.Kind == DataConnectorAuthenticationKind.CustomHeader
            && authentication.CustomHeaderName is not ("X-Api-Key" or "Api-Key"))
        {
            throw new ArgumentException("Custom authentication header must be X-Api-Key or Api-Key.");
        }

        if (platform.SourceSettings.PageSize is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(platform), "Connector page size must be between 1 and 10000.");
        }

        if (kind == DataConnectorKind.PostgreSql)
        {
            var table = platform.SourceSettings.SourceTable?.Split('.', StringSplitOptions.TrimEntries) ?? [];
            if (table.Length != 2
                || !IsBareIdentifier(table[0])
                || !IsBareIdentifier(table[1])
                || !IsBareIdentifier(platform.SourceSettings.CursorColumn)
                || platform.SourceSettings.CursorType?.Trim().ToLowerInvariant() is not
                    ("int64" or "timestamptz" or "uuid" or "text")
                || !platform.SourceSettings.CursorIsCommitMonotonic)
            {
                throw new ArgumentException(
                    "PostgreSQL connectors require bare schema.table and cursor identifiers, a supported cursor type, and an explicit commit-monotonic cursor contract.");
            }
        }

        if (kind == DataConnectorKind.HubSpot
            && (platform.SourceSettings.PageSize > 200
                || (platform.SourceSettings.Properties?.Count ?? 0) > 100
                || (platform.SourceSettings.Properties ?? []).Any(property => !IsBareIdentifier(property))))
        {
            throw new ArgumentException("HubSpot source properties or page size are invalid.");
        }
    }

    private static bool IsBareIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 255
        && (char.IsAsciiLetter(value[0]) || value[0] == '_')
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}

/// <summary>The validated mutable definition accepted by the connector aggregate.</summary>
public sealed record DataConnectorDefinition(
    string Name,
    string? Description,
    string Owner,
    IReadOnlyList<string> Tags,
    DataConnectorKind Kind,
    string EndpointUrl,
    string? CredentialEnvironmentVariable,
    RestResponseFormat RestResponseFormat,
    string TargetSchema,
    string TargetTable,
    long MinimumRows,
    IReadOnlyList<string> RequiredColumns,
    IReadOnlyList<string> NotNullColumns,
    bool Enabled,
    int? RefreshIntervalSeconds,
    DataConnectorPlatformDefinition? Platform = null);

/// <summary>Non-secret, adapter-specific source configuration.</summary>
public sealed record DataConnectorSourceSettings(
    string? SourceTable = null,
    string? CursorColumn = null,
    string? CursorType = null,
    int PageSize = 100,
    IReadOnlyList<string>? Properties = null,
    bool CursorIsCommitMonotonic = false);

/// <summary>Approved authentication configuration containing secret references, never values.</summary>
public sealed record DataConnectorAuthentication(
    DataConnectorAuthenticationKind Kind = DataConnectorAuthenticationKind.None,
    string? SecretReference = null,
    string? UsernameSecretReference = null,
    string? PasswordSecretReference = null,
    string? ClientIdSecretReference = null,
    string? ClientSecretReference = null,
    string? RefreshTokenSecretReference = null,
    string? ClientCertificateSecretReference = null,
    string? CertificatePasswordSecretReference = null,
    string? CustomHeaderName = null);

/// <summary>One declarative source-to-target field operation.</summary>
public sealed record DataConnectorFieldMapping(
    string Source,
    string Target,
    DataConnectorTransformKind Transform = DataConnectorTransformKind.None);

/// <summary>Versioned connector platform settings shared by every protocol adapter.</summary>
public sealed record DataConnectorPlatformDefinition(
    string AdapterId,
    int AdapterVersion,
    DataConnectorReadMode ReadMode,
    DataConnectorSchemaPolicy SchemaPolicy,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<DataConnectorFieldMapping> FieldMappings,
    DataConnectorSourceSettings SourceSettings,
    DataConnectorAuthentication Authentication,
    int MaxAttempts = 5,
    int RetryBaseSeconds = 30,
    int RetryMaxSeconds = 3_600)
{
    public static DataConnectorPlatformDefinition Legacy(DataConnectorDefinition definition) => new(
        definition.Kind switch
        {
            DataConnectorKind.Rest => "lakehold.rest",
            DataConnectorKind.Grpc => "lakehold.grpc",
            DataConnectorKind.PostgreSql => "lakehold.postgresql",
            DataConnectorKind.HubSpot => "lakehold.hubspot-contacts",
            _ => throw new ArgumentOutOfRangeException(nameof(definition)),
        },
        1,
        definition.Kind is DataConnectorKind.PostgreSql or DataConnectorKind.HubSpot
            ? DataConnectorReadMode.Incremental
            : DataConnectorReadMode.FullSnapshot,
        DataConnectorSchemaPolicy.Reject,
        [],
        [],
        new DataConnectorSourceSettings(),
        definition.CredentialEnvironmentVariable is null
            ? new DataConnectorAuthentication()
            : new DataConnectorAuthentication(
                DataConnectorAuthenticationKind.Bearer,
                $"env://{definition.CredentialEnvironmentVariable}"));
}

/// <summary>Durable lineage and quality evidence for one connector refresh.</summary>
public sealed class DataConnectorRun
{
    private DataConnectorRun()
    {
    }

    public int Id { get; private set; }

    public int DataConnectorId { get; private set; }

    public DataConnectorTrigger Trigger { get; private set; }

    public DataConnectorRunStatus Status { get; private set; }

    public string NodeId { get; private set; } = string.Empty;

    public string LeaseToken { get; private set; } = string.Empty;

    public DateTimeOffset StartedUtc { get; private set; }

    public DateTimeOffset? CompletedUtc { get; private set; }

    public long RowsRead { get; private set; }

    public long RowsPublished { get; private set; }

    public bool? QualityPassed { get; private set; }

    public string? SourceVersion { get; private set; }

    public string? Error { get; private set; }

    public string? InputCheckpoint { get; private set; }

    public string? ProposedCheckpoint { get; private set; }

    public string? ReplayKey { get; private set; }

    public DataConnector DataConnector { get; private set; } = null!;

    public static DataConnectorRun Start(
        int connectorId,
        DataConnectorTrigger trigger,
        string nodeId,
        string leaseToken,
        DateTimeOffset now,
        string? inputCheckpoint = null) => new()
        {
            DataConnectorId = connectorId,
            Trigger = trigger,
            Status = DataConnectorRunStatus.Running,
            NodeId = nodeId,
            LeaseToken = leaseToken,
            StartedUtc = now,
            InputCheckpoint = Normalize(inputCheckpoint, 4_000),
        };

    public void Succeed(
        DateTimeOffset now,
        long rowsRead,
        long rowsPublished,
        string? sourceVersion,
        string? proposedCheckpoint = null,
        string? replayKey = null)
    {
        EnsureRunning();
        Status = DataConnectorRunStatus.Succeeded;
        CompletedUtc = now;
        RowsRead = rowsRead;
        RowsPublished = rowsPublished;
        QualityPassed = true;
        SourceVersion = Normalize(sourceVersion, 512);
        ProposedCheckpoint = Normalize(proposedCheckpoint, 4_000);
        ReplayKey = Normalize(replayKey, 512);
        Error = null;
    }

    public void Fail(
        DateTimeOffset now,
        long rowsRead,
        string? sourceVersion,
        bool? qualityPassed,
        string error,
        bool deadLettered = false,
        string? proposedCheckpoint = null,
        string? replayKey = null)
    {
        EnsureRunning();
        Status = deadLettered ? DataConnectorRunStatus.DeadLettered : DataConnectorRunStatus.Failed;
        CompletedUtc = now;
        RowsRead = rowsRead;
        RowsPublished = 0;
        QualityPassed = qualityPassed;
        SourceVersion = Normalize(sourceVersion, 512);
        ProposedCheckpoint = Normalize(proposedCheckpoint, 4_000);
        ReplayKey = Normalize(replayKey, 512);
        Error = Normalize(error, 4_000) ?? "Connector refresh failed.";
    }

    private void EnsureRunning()
    {
        if (Status != DataConnectorRunStatus.Running)
        {
            throw new InvalidOperationException("Only a running connector execution can reach a terminal state.");
        }
    }

    private static string? Normalize(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maxLength)];
}

/// <summary>A named, reusable query.</summary>
public sealed class SavedQuery
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    /// <summary>
    ///     Catalog this query is bound to. Nullable only so databases created before catalog-scoped
    ///     saved queries can be upgraded without inventing a binding for dormant legacy rows.
    ///     Every query created through the application has a value.
    /// </summary>
    public int? CatalogId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public required string Sql { get; set; }

    /// <summary>Workbench language of <see cref="Sql"/>; defaults to SQL for pre-LINQ history.</summary>
    public string Language { get; set; } = "sql";

    /// <summary>Optimistic revision of the saved definition, starting at one.</summary>
    public int Revision { get; set; }

    /// <summary>
    ///     Optimistic version of the whole record. Unlike <see cref="Revision"/>, this advances for
    ///     publication-state changes as well as authored-definition changes.
    /// </summary>
    public int ConcurrencyVersion { get; set; }

    /// <summary>Token that first saved the query, or null for OIDC and legacy callers.</summary>
    public int? CreatedByTokenId { get; set; }

    /// <summary>Token that last changed the definition, or null for OIDC and legacy callers.</summary>
    public int? UpdatedByTokenId { get; set; }

    /// <summary>Schema of the published DuckLake view, when this query has one.</summary>
    public string? PublishedSchema { get; set; }

    /// <summary>Name of the published DuckLake view, when this query has one.</summary>
    public string? PublishedViewName { get; set; }

    /// <summary>Catalog schema fingerprint used to compile the published definition.</summary>
    public string? PublishedSchemaFingerprint { get; set; }

    /// <summary>Revision whose SQL the published view currently contains.</summary>
    public int? PublishedRevision { get; set; }

    public DateTimeOffset? PublishedUtc { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public Tenant Tenant { get; set; } = null!;

    public LakeCatalog? Catalog { get; set; }
}

/// <summary>
///     An outbound change-data-capture subscription: a webhook fired whenever a catalog commits
///     snapshots that changed a subscribed table.
/// </summary>
/// <remarks>
///     <para>
///         The delivery cursor is <see cref="LastDeliveredSnapshot"/> — everything up to and
///         including it has been delivered. The dispatcher reads the next window from the cursor
///         plus one, so a change is delivered at least once and never re-delivered after a
///         successful post. Delivery is at-least-once, not exactly-once: a crash between the post
///         and the cursor write re-sends the window, which consumers must tolerate (the payload's
///         snapshot ids make deduplication cheap).
///     </para>
///     <para>
///         <see cref="Secret"/> signs payloads so the receiver can authenticate them. It is stored
///         here because the dispatcher must read it on every delivery, but it must never appear in
///         an API response or a log — the DTO layer omits it deliberately.
///     </para>
/// </remarks>
public sealed class ChangeSubscription
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    /// <summary>Catalog whose changes are watched.</summary>
    public required string CatalogName { get; set; }

    /// <summary>
    ///     Table to watch, or null to watch every base table in the catalog.
    /// </summary>
    public string? TableName { get; set; }

    /// <summary>Schema of <see cref="TableName"/>. Defaults to <c>main</c>.</summary>
    public string SchemaName { get; set; } = "main";

    /// <summary>Endpoint the signed change payload is posted to.</summary>
    public required string EndpointUrl { get; set; }

    /// <summary>
    ///     Shared secret used to HMAC-sign each delivery. Never returned by the API and never
    ///     logged.
    /// </summary>
    public required string Secret { get; set; }

    /// <summary>Whether the dispatcher considers this subscription at all.</summary>
    public bool Active { get; set; } = true;

    /// <summary>
    ///     Highest snapshot id whose changes have been successfully delivered. Initialised to the
    ///     catalog's latest snapshot at creation, so a new subscription starts from "now" rather
    ///     than replaying the catalog's entire history into an unsuspecting endpoint.
    /// </summary>
    public long LastDeliveredSnapshot { get; set; }

    /// <summary>Consecutive delivery failures, reset on success. Drives observability, not retry.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>When the last delivery attempt happened, successful or not.</summary>
    public DateTimeOffset? LastAttemptUtc { get; set; }

    /// <summary>Failure message from the last attempt, cleared on success.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public Tenant Tenant { get; set; } = null!;

    public ICollection<ChangeDelivery> Deliveries { get; } = [];
}

/// <summary>
///     Durable identity and lease for one subscription snapshot. HTTP retries reuse this row's
///     delivery id and creation time, so an expired worker lease can replay safely on another node.
/// </summary>
public sealed class ChangeDelivery
{
    public int Id { get; set; }

    public int SubscriptionId { get; set; }

    /// <summary>Stable public id shared by every attempt of this logical delivery.</summary>
    public required string DeliveryId { get; set; }

    /// <summary>The single source snapshot represented by this delivery.</summary>
    public long SnapshotId { get; set; }

    /// <summary>Stable logical-delivery creation time; attempts use a fresh signed timestamp.</summary>
    public DateTimeOffset CreatedUtc { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset? LastAttemptUtc { get; set; }

    public DateTimeOffset? NextAttemptUtc { get; set; }

    public string? LeaseOwner { get; set; }

    public DateTimeOffset? LeaseExpiresUtc { get; set; }

    public DateTimeOffset? DeliveredUtc { get; set; }

    /// <summary>Exact UTF-8 JSON body reused for every retry; never written to logs.</summary>
    public byte[]? Payload { get; set; }

    public string? LastError { get; set; }

    /// <summary>Optimistic claim token; concurrent nodes may update only the version they read.</summary>
    public long Version { get; set; }

    public ChangeSubscription Subscription { get; set; } = null!;
}

/// <summary>
///     Durable pull consumer whose checkpoint participates in snapshot-retention safety. Replicas
///     register here before bootstrap and advance only after their target transaction commits.
/// </summary>
public sealed class CdcConsumer
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    public required string CatalogName { get; set; }

    public required string Name { get; set; }

    public long LastAppliedSnapshot { get; set; }

    public bool Active { get; set; } = true;

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public Tenant Tenant { get; set; } = null!;
}

/// <summary>
///     One executed statement. Doubles as the audit log and as the source for the UI's history
///     panel, so it records both the outcome and the failure reason.
/// </summary>
public sealed class QueryRun
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    public required string CatalogName { get; set; }

    public required string Sql { get; set; }

    /// <summary>Workbench language of <see cref="Sql"/>; defaults to SQL for pre-LINQ history.</summary>
    public string Language { get; set; } = "sql";

    public DateTimeOffset StartedUtc { get; set; }

    public double ElapsedMilliseconds { get; set; }

    public int RowCount { get; set; }

    public bool Succeeded { get; set; }

    /// <summary>Failure message when <see cref="Succeeded"/> is false.</summary>
    public string? Error { get; set; }

    /// <summary>
    ///     The <see cref="ApiToken"/> that ran the statement, for audit. Nullable — and no foreign key
    ///     — so the pre-auth history that predates tokens survives, and a revoked or deleted token
    ///     never takes its audit trail down with it.
    /// </summary>
    public int? TokenId { get; set; }

    public Tenant Tenant { get; set; } = null!;
}

/// <summary>What an <see cref="ApiToken"/> may do — its capability, kept distinct from its subject.</summary>
public enum TokenScope
{
    /// <summary>Acts as one tenant. The overwhelming majority of tokens.</summary>
    Tenant,

    /// <summary>
    ///     Provisions tenants and catalogs and mints tenant tokens. It cannot itself query, run
    ///     maintenance, or eject — every data path requires a tenant-scoped token, so the credential
    ///     always names the tenant whose data is reachable.
    /// </summary>
    Instance,
}

/// <summary>
///     A bearer credential for the HTTP API. The token string is shown once at creation and never
///     stored; only its <see cref="Prefix"/> and a SHA-256 <see cref="SecretHash"/> are persisted, so
///     a database read never yields a usable credential.
/// </summary>
/// <remarks>
///     Capability (<see cref="Scope"/>, <see cref="ReadOnly"/>) and subject (<see cref="TenantId"/>,
///     <see cref="CatalogName"/>) are separate axes — see <c>docs/AUTHENTICATION.md</c>. Generation and
///     verification live in <see cref="Security.ApiTokenFactory"/>.
/// </remarks>
public sealed class ApiToken
{
    public int Id { get; set; }

    /// <summary>Capability: acts as a tenant, or provisions the instance.</summary>
    public TokenScope Scope { get; set; }

    /// <summary>Owning tenant, or null for an instance-scoped token, which belongs to no tenant.</summary>
    public int? TenantId { get; set; }

    /// <summary>
    ///     Optional least-privilege narrowing for a tenant token: null grants every catalog in the
    ///     tenant, a value restricts the token to that one catalog. Subject, not capability — orthogonal
    ///     to <see cref="Scope"/>, and always null for an instance token.
    /// </summary>
    public string? CatalogName { get; set; }

    /// <summary>Human-facing label. Not a secret and not an identifier.</summary>
    public required string Name { get; set; }

    /// <summary>
    ///     The token's public prefix (<c>lkh_&lt;tenant&gt;_</c>, or <c>lkh_admin_</c>), stored in the
    ///     clear. It narrows a lookup to a candidate set before the secret is verified, and makes a
    ///     leaked token identifiable in a log without being usable.
    /// </summary>
    public required string Prefix { get; set; }

    /// <summary>SHA-256 of the full token, lower-hex. The token itself is never stored.</summary>
    public required string SecretHash { get; set; }

    /// <summary>Whether the credential produces a read-only catalog attachment.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    ///     What the credential may do within its tenant. Defaults to <see cref="TokenRole.Owner"/>,
    ///     which is what every token minted before roles existed effectively was — so an existing
    ///     deployment's credentials keep working across the upgrade.
    /// </summary>
    public TokenRole Role { get; set; } = TokenRole.Owner;

    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>Optional expiry; a token past this instant is refused.</summary>
    public DateTimeOffset? ExpiresUtc { get; set; }

    /// <summary>Set when the token is revoked; a revoked token is refused thereafter.</summary>
    public DateTimeOffset? RevokedUtc { get; set; }

    /// <summary>Updated opportunistically, off the request path.</summary>
    public DateTimeOffset? LastUsedUtc { get; set; }

    /// <summary>Owning tenant, or null for an instance-scoped token.</summary>
    public Tenant? Tenant { get; set; }
}

/// <summary>Whether a person's membership of a tenant currently grants anything.</summary>
public enum MemberStatus
{
    /// <summary>
    ///     Known to LakeHold and granted nothing. The state a new identity lands in: signing in
    ///     proves who someone is, not that they should reach a workspace. An owner promotes them.
    /// </summary>
    Pending = 0,

    /// <summary>Membership is in force; <see cref="TenantMember.Role"/> says what it grants.</summary>
    Active = 1,

    /// <summary>
    ///     Retained but granting nothing. Distinct from deletion so the audit trail keeps the person
    ///     who ran past queries, and so re-admitting someone is not indistinguishable from a new
    ///     arrival.
    /// </summary>
    Suspended = 2,
}

/// <summary>
///     A person's membership of one tenant.
/// </summary>
/// <remarks>
///     LakeHold federates <em>authentication</em> and owns <em>authorization</em>. It never stores a
///     password: the identity provider proves who someone is, and this record says what that
///     identity may reach. Before it existed, the answer came from a claim, which meant access could
///     only be granted by persuading the provider to emit something — invisible from inside the
///     product, impossible to list, and impossible to revoke here.
///     <para>
///         Keyed on issuer and subject rather than on an email address or username. Those are
///         mutable, and a re-used address would silently inherit the previous holder's access;
///         <c>sub</c> is stable and opaque for exactly this reason.
///     </para>
/// </remarks>
public sealed class TenantMember
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    /// <summary>The <c>iss</c> that vouched for this person. Part of the identity, not decoration.</summary>
    /// <remarks>
    ///     Two providers can mint the same subject, so a membership is only meaningful paired with
    ///     the issuer that asserted it. Repointing a deployment at a new provider therefore leaves
    ///     the old memberships inert rather than silently transferring them to whoever now holds
    ///     that subject.
    /// </remarks>
    public required string Issuer { get; set; }

    /// <summary>The provider's stable subject identifier for this person.</summary>
    public required string Subject { get; set; }

    /// <summary>Last display name seen, for the members list. Never used to identify anyone.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Last email seen, for the members list. Never used to identify anyone.</summary>
    public string? Email { get; set; }

    /// <summary>What an <see cref="MemberStatus.Active"/> membership grants within the tenant.</summary>
    public TokenRole Role { get; set; } = TokenRole.Reader;

    public MemberStatus Status { get; set; } = MemberStatus.Pending;

    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>When this identity last authenticated. Null until they first sign in.</summary>
    public DateTimeOffset? LastSeenUtc { get; set; }

    public Tenant? Tenant { get; set; }
}
