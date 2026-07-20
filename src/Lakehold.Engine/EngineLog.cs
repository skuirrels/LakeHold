using Microsoft.Extensions.Logging;
using Lakehold.Engine.Catalog;

namespace Lakehold.Engine;

/// <summary>
///     Source-generated log messages for the data plane. Compute sessions are started and evicted
///     on the query hot path, so these avoid the boxing and format-string parsing that the
///     <c>ILogger.LogX</c> extension methods incur per call.
/// </summary>
internal static partial class EngineLog
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Started Duckling for catalog {Catalog} ({MetadataKind}, readOnly={ReadOnly})")]
    public static partial void DucklingStarted(
        ILogger logger,
        string catalog,
        CatalogMetadataKind metadataKind,
        bool readOnly);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Stopped Duckling for catalog {Catalog}")]
    public static partial void DucklingStopped(ILogger logger, string catalog);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Error,
        Message = "Failed to start Duckling for catalog {Catalog}")]
    public static partial void DucklingStartFailed(ILogger logger, Exception exception, string catalog);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Information,
        Message = "Evicting idle Duckling for catalog {Catalog}")]
    public static partial void DucklingEvictedIdle(ILogger logger, string catalog);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Information,
        Message = "Evicting Duckling for catalog {Catalog} to stay within the warm-session ceiling")]
    public static partial void DucklingEvictedOverflow(ILogger logger, string catalog);
}
