using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.Mcp;

/// <summary>The effective MCP settings used for one request.</summary>
public sealed record McpRuntimeSettings(
    bool Enabled,
    bool AllowWrites,
    int MaxRowsPerResult,
    string PublicBaseUrl,
    string Route,
    int Version,
    DateTimeOffset? UpdatedUtc)
{
    /// <summary>Bounds a requested page by the MCP and engine ceilings.</summary>
    public int BoundPageSize(int requested, int engineCeiling)
    {
        var ceiling = MaxRowsPerResult > 0 ? Math.Min(MaxRowsPerResult, engineCeiling) : engineCeiling;
        return Math.Clamp(requested, 1, ceiling);
    }
}

/// <summary>Raised when an operator saves an invalid system setting.</summary>
public sealed class SystemSettingsValidationException(string message) : Exception(message);

/// <summary>Raised when an operator saves over a newer settings revision.</summary>
public sealed class SystemSettingsConflictException : Exception
{
    public SystemSettingsConflictException(string message)
        : base(message)
    {
    }

    public SystemSettingsConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
///     Reads and writes MCP runtime settings from the shared control plane.
/// </summary>
/// <remarks>
///     No process cache is used. Each request observes the committed singleton row, so a save on one
///     API node is effective on every node without a restart. Static configuration is only the
///     bootstrap value for an installation that has not saved settings yet.
/// </remarks>
public sealed class McpRuntimeSettingsStore(
    ControlPlaneContext context,
    IOptions<McpOptions> bootstrap,
    TimeProvider clock)
{
    private const int SingletonId = 1;

    /// <summary>Loads the persisted settings, or configuration defaults before the first save.</summary>
    public async Task<McpRuntimeSettings> GetAsync(CancellationToken cancellationToken)
    {
        var row = await context.SystemSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == SingletonId, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? FromBootstrap(bootstrap.Value) : FromEntity(row, bootstrap.Value.Route);
    }

    /// <summary>Persists a complete replacement at the caller's expected version.</summary>
    public async Task<McpRuntimeSettings> SaveAsync(
        bool enabled,
        bool allowWrites,
        int maxRowsPerResult,
        string? publicBaseUrl,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        var normalizedBaseUrl = publicBaseUrl?.Trim().TrimEnd('/') ?? string.Empty;
        Validate(maxRowsPerResult, normalizedBaseUrl);
        var row = await context.SystemSettings
            .SingleOrDefaultAsync(s => s.Id == SingletonId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            if (expectedVersion != 0)
            {
                throw Conflict();
            }

            row = new SystemSettings
            {
                Id = SingletonId,
                ConcurrencyVersion = 1,
            };
            context.SystemSettings.Add(row);
        }
        else
        {
            if (row.ConcurrencyVersion != expectedVersion)
            {
                throw Conflict();
            }

            row.ConcurrencyVersion++;
        }

        row.McpEnabled = enabled;
        row.McpAllowWrites = allowWrites;
        row.McpMaxRowsPerResult = maxRowsPerResult;
        row.McpPublicBaseUrl = normalizedBaseUrl.Length == 0 ? null : normalizedBaseUrl;
        row.UpdatedUtc = clock.GetUtcNow();

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new SystemSettingsConflictException(
                "System settings changed before this save completed. Reload them and try again.",
                ex);
        }
        catch (DbUpdateException ex) when (expectedVersion == 0)
        {
            // Two nodes can race to create the singleton. The primary key makes one lose; report the
            // same optimistic conflict as an update race instead of leaking a provider error.
            throw new SystemSettingsConflictException(
                "System settings changed before this save completed. Reload them and try again.",
                ex);
        }

        return FromEntity(row, bootstrap.Value.Route);
    }

    private static McpRuntimeSettings FromBootstrap(McpOptions options) =>
        new(
            options.Enabled,
            options.AllowWrites,
            options.MaxRowsPerResult,
            options.PublicBaseUrl,
            options.Route,
            Version: 0,
            UpdatedUtc: null);

    private static McpRuntimeSettings FromEntity(SystemSettings settings, string route) =>
        new(
            settings.McpEnabled,
            settings.McpAllowWrites,
            settings.McpMaxRowsPerResult,
            settings.McpPublicBaseUrl ?? string.Empty,
            route,
            settings.ConcurrencyVersion,
            settings.UpdatedUtc);

    private static void Validate(int maxRowsPerResult, string? publicBaseUrl)
    {
        if (maxRowsPerResult is < 1 or > 10_000)
        {
            throw new SystemSettingsValidationException(
                "MCP maximum rows must be between 1 and 10,000.");
        }

        if (publicBaseUrl?.Length > SystemSettings.McpPublicBaseUrlMaxLength)
        {
            throw new SystemSettingsValidationException(
                $"MCP public base URL must not exceed {SystemSettings.McpPublicBaseUrlMaxLength} characters.");
        }

        if (!string.IsNullOrWhiteSpace(publicBaseUrl)
            && (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || uri.Query.Length > 0
                || uri.Fragment.Length > 0))
        {
            throw new SystemSettingsValidationException(
                "MCP public base URL must be an absolute HTTP or HTTPS URL without a query or fragment.");
        }
    }

    private static SystemSettingsConflictException Conflict() =>
        new("System settings changed since they were loaded. Reload them and try again.");
}
