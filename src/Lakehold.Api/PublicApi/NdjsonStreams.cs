using System.Text.Json;
using Lakehold.ControlPlane.Data;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Execution;
using Lakehold.ControlPlane.Security;

namespace Lakehold.Api.PublicApi;

/// <summary>Writes the public query stream without materialising its rows.</summary>
internal sealed class QueryNdjsonResult(
    LakehouseService lakehouse,
    string tenant,
    string catalog,
    string sql,
    QueryAuditContext audit,
    ILogger logger) : IResult
{
    internal QueryNdjsonResult(
        LakehouseService lakehouse,
        string tenant,
        string catalog,
        string sql,
        int? tokenId,
        ILogger logger)
        : this(
            lakehouse,
            tenant,
            catalog,
            sql,
            QueryAuditContext.FromToken(tokenId, Lakehold.ControlPlane.Model.QueryOrigin.Rest),
            logger)
    {
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        Prepare(httpContext.Response);

        try
        {
            var rows = await lakehouse.StreamAsync(
                    tenant,
                    catalog,
                    sql,
                    async (columns, cancellationToken) =>
                    {
                        await Ndjson.WriteAsync(
                                httpContext.Response,
                                new QueryStreamSchemaDto(
                                    "schema",
                                    [.. columns.Select(column =>
                                        new ColumnDto(column.Name, column.DataType, column.ClrType.FullName ?? column.ClrType.Name))]),
                                cancellationToken)
                            .ConfigureAwait(false);
                    },
                    async (row, cancellationToken) =>
                    {
                        await Ndjson.WriteAsync(
                                httpContext.Response,
                                new QueryStreamRowDto("row", row.ToArray()),
                                cancellationToken)
                            .ConfigureAwait(false);
                    },
                    maxRows: 0,
                    httpContext.RequestAborted,
                    readOnly: true,
                    audit.TokenId,
                    audit)
                .ConfigureAwait(false);

            await Ndjson.WriteAsync(
                    httpContext.Response,
                    new QueryStreamCompleteDto("complete", rows),
                    httpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            // A disconnected consumer is the normal streaming cancellation path. There is no peer
            // left to receive an error envelope and the linked token has already stopped DuckDB.
        }
        catch (Exception exception)
        {
            await Ndjson.TryWriteErrorAsync(httpContext, exception, logger).ConfigureAwait(false);
        }
    }

    private static void Prepare(HttpResponse response)
    {
        response.ContentType = Ndjson.ContentType;
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Accel-Buffering"] = "no";
    }
}

/// <summary>Writes a finite, snapshot-frozen CDC range as NDJSON.</summary>
internal sealed class ChangeNdjsonResult(
    LakehouseService lakehouse,
    string tenant,
    string catalog,
    ChangeFeedPage firstPage,
    int pageSize,
    ILogger logger) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        response.ContentType = Ndjson.ContentType;
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            await Ndjson.WriteAsync(
                    response,
                    new ChangeStreamStartDto(
                        "stream",
                        firstPage.Schema,
                        firstPage.Table,
                        firstPage.FromSnapshot,
                        firstPage.ToSnapshot),
                    httpContext.RequestAborted)
                .ConfigureAwait(false);

            var count = 0L;
            var page = firstPage;
            while (true)
            {
                foreach (var change in page.Changes)
                {
                    await Ndjson.WriteAsync(
                            response,
                            new ChangeStreamItemDto("change", ToDto(change)),
                            httpContext.RequestAborted)
                        .ConfigureAwait(false);
                    count++;
                }

                if (page.NextCursor is null)
                {
                    break;
                }

                page = await lakehouse.GetChangesAsync(
                        tenant,
                        catalog,
                        page.Schema,
                        page.Table,
                        page.FromSnapshot,
                        page.ToSnapshot,
                        pageSize,
                        page.NextCursor,
                        httpContext.RequestAborted)
                    .ConfigureAwait(false);
            }

            await Ndjson.WriteAsync(
                    response,
                    new ChangeStreamCompleteDto("complete", count, firstPage.ToSnapshot),
                    httpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            // See QueryNdjsonResult: cancellation is terminal and must not be disguised as success.
        }
        catch (Exception exception)
        {
            await Ndjson.TryWriteErrorAsync(httpContext, exception, logger).ConfigureAwait(false);
        }
    }

    private static ChangeDto ToDto(TableChange change)
        => new(change.SnapshotId, change.RowId, ChangeTypeName(change.Change), change.Row);

    private static string ChangeTypeName(ChangeType change) => change switch
    {
        ChangeType.Insert => "insert",
        ChangeType.Delete => "delete",
        ChangeType.UpdatePreimage => "update_preimage",
        ChangeType.UpdatePostimage => "update_postimage",
        _ => "unknown",
    };
}

/// <summary>Shared, one-object-per-line encoding for the streaming public contract.</summary>
internal static class Ndjson
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Action<ILogger, string, Exception?> LogUnexpectedFailure = LoggerMessage.Define<string>(
        LogLevel.Error,
        new EventId(1, "UnexpectedStreamFailure"),
        "LakeHold stream failed unexpectedly. RequestId: {RequestId}");

    public const string ContentType = "application/x-ndjson";

    public static async Task WriteAsync<T>(HttpResponse response, T value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        await response.Body.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await response.Body.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task TryWriteErrorAsync(HttpContext httpContext, Exception exception, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (httpContext.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        var (status, code, detail) = Failure(exception);
        if (status >= StatusCodes.Status500InternalServerError)
        {
            LogUnexpectedFailure(logger, httpContext.TraceIdentifier, exception);
        }

        try
        {
            if (!httpContext.Response.HasStarted)
            {
                await PublicApiProblems.Create(httpContext, status, detail, code)
                    .ExecuteAsync(httpContext)
                    .ConfigureAwait(false);
                return;
            }

            await WriteAsync(
                    httpContext.Response,
                    new StreamErrorDto(
                        "error",
                        code,
                        httpContext.TraceIdentifier,
                        detail),
                    httpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            // The peer disconnected while the terminal error was being written.
        }
    }

    private static (int Status, string Code, string Detail) Failure(Exception exception) => exception switch
    {
        CatalogNotFoundException => (
            StatusCodes.Status404NotFound,
            "catalog_not_found",
            PublicApiProblems.BoundedDetail(exception.Message)),
        ArgumentException => (
            StatusCodes.Status400BadRequest,
            "invalid_stream_request",
            PublicApiProblems.BoundedDetail(exception.Message)),
        DuckDB.NET.Data.DuckDBException => (
            StatusCodes.Status400BadRequest,
            "query_failed",
            PublicApiProblems.BoundedDetail(exception.Message)),
        _ => (
            StatusCodes.Status500InternalServerError,
            "stream_failed",
            "The stream failed unexpectedly. Use the request identifier to find the operator log."),
    };
}
