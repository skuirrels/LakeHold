using System.Globalization;
using Lakehold.Api.Auth;
using Lakehold.Api.Importing;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Security;
using Lakehold.Engine.Catalog;
using Microsoft.AspNetCore.Mvc;

namespace Lakehold.Api.Endpoints;

/// <summary>Browser upload and tabular-file-to-table endpoints.</summary>
public static class TabularImportEndpoints
{
    /// <summary>Adds the catalog-scoped CSV/XLSX import route and its CSV compatibility alias.</summary>
    public static void MapTabularImportEndpoints(this RouteGroupBuilder tenants, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        tenants.MapPost(
                "/{tenantSlug}/catalogs/{catalogName}/imports/files",
                (Func<HttpContext, string, string, TabularUploadService, CancellationToken, Task<IResult>>)ImportAsync)
            .RequireCapability(Capability.TenantWrite)
            .WithMetadata(new RequestSizeLimitAttribute(maxBytes))
            .Produces<TabularImportDto>()
            .WithSummary("Uploads a CSV or XLSX file and creates a new DuckLake table.");

        tenants.MapPost(
                "/{tenantSlug}/catalogs/{catalogName}/imports/csv",
                (Func<HttpContext, string, string, TabularUploadService, CancellationToken, Task<IResult>>)ImportCsvAsync)
            .RequireCapability(Capability.TenantWrite)
            .WithMetadata(new RequestSizeLimitAttribute(maxBytes))
            .Produces<TabularImportDto>()
            .WithSummary("Compatibility alias for CSV uploads.");
    }

    internal static async Task<IResult> ImportAsync(
        HttpContext http,
        string tenantSlug,
        string catalogName,
        TabularUploadService uploads,
        CancellationToken cancellationToken)
        => await ImportCoreAsync(
                http,
                tenantSlug,
                catalogName,
                uploads,
                requiredFormat: null,
                cancellationToken)
            .ConfigureAwait(false);

    internal static async Task<IResult> ImportCsvAsync(
        HttpContext http,
        string tenantSlug,
        string catalogName,
        TabularUploadService uploads,
        CancellationToken cancellationToken)
        => await ImportCoreAsync(
                http,
                tenantSlug,
                catalogName,
                uploads,
                TabularFileFormat.Csv,
                cancellationToken)
            .ConfigureAwait(false);

    private static async Task<IResult> ImportCoreAsync(
        HttpContext http,
        string tenantSlug,
        string catalogName,
        TabularUploadService uploads,
        TabularFileFormat? requiredFormat,
        CancellationToken cancellationToken)
    {
        if (!TryReadRequest(http.Request.Query, out var request, out var validationError))
        {
            return Results.BadRequest(validationError);
        }

        if (requiredFormat is { } format && request.Format != format)
        {
            return Results.BadRequest(
                "The compatibility /imports/csv route accepts CSV files only. "
                + "Use /imports/files for XLSX workbooks.");
        }

        if (!IsSupportedContentType(http.Request.ContentType, request.Format))
        {
            return Results.BadRequest(
                request.Format == TabularFileFormat.Csv
                    ? "CSV import requires a text/csv or application/octet-stream body."
                    : "XLSX import requires the Open XML spreadsheet or application/octet-stream content type.");
        }

        try
        {
            var principal = http.GetLakeholdPrincipal();
            var result = await uploads
                .ImportAsync(
                    tenantSlug,
                    catalogName,
                    http.Request.Body,
                    http.Request.ContentLength,
                    request.FileName,
                    request.Format,
                    request.Schema,
                    request.Table,
                    request.AutomaticMode,
                    request.Options,
                    request.Worksheet,
                    principal.TokenId,
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(TabularImportDto.From(result));
        }
        catch (TabularUploadTooLargeException ex)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "File upload rejected",
                detail: ex.Message);
        }
        catch (TabularScratchCapacityException ex)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status507InsufficientStorage,
                title: "Import scratch capacity unavailable",
                detail: ex.Message);
        }
        catch (CatalogNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }
        catch (CsvImportException ex)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: ex.IsParserError ? "CSV parsing failed" : "CSV import failed",
                detail: ex.Message,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = ex.Code,
                });
        }
        catch (XlsxImportException ex)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "XLSX import failed",
                detail: ex.Message,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = ex.Code,
                });
        }
        catch (DuckDB.NET.Data.DuckDBException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "File import failed",
                detail: "DuckDB could not import the file with the selected settings.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "tabular_import_error",
                });
        }
    }

    private static bool TryReadRequest(
        IQueryCollection query,
        out TabularImportForm request,
        out string error)
    {
        var fileName = Value(query, "fileName");
        var schema = Value(query, "schema");
        var table = Value(query, "table");
        if (string.IsNullOrWhiteSpace(fileName)
            || string.IsNullOrWhiteSpace(schema)
            || string.IsNullOrWhiteSpace(table))
        {
            request = null!;
            error = "File name, schema, and table are required.";
            return false;
        }

        if (!TryFileFormat(fileName, out var format))
        {
            request = null!;
            error = "File name must end in .csv or .xlsx. Legacy .xls workbooks are not supported.";
            return false;
        }

        var mode = Value(query, "mode");
        if (format == TabularFileFormat.Xlsx)
        {
            if (!string.IsNullOrEmpty(mode)
                && !string.Equals(mode, "automatic", StringComparison.Ordinal))
            {
                request = null!;
                error = "XLSX imports use automatic mode; advanced reader settings are available for CSV files only.";
                return false;
            }

            var worksheet = Value(query, "worksheet");
            if (worksheet.Length > 255 || worksheet.Contains('\0'))
            {
                request = null!;
                error = "XLSX worksheet names cannot exceed 255 characters or contain a NUL.";
                return false;
            }

            request = new TabularImportForm(
                fileName,
                format,
                schema,
                table,
                AutomaticMode: true,
                Options: new CsvReadOptions(),
                Worksheet: worksheet);
            error = string.Empty;
            return true;
        }

        if (string.IsNullOrEmpty(mode) || string.Equals(mode, "automatic", StringComparison.Ordinal))
        {
            request = new TabularImportForm(
                fileName,
                format,
                schema,
                table,
                AutomaticMode: true,
                Options: new CsvReadOptions(),
                Worksheet: null);
            error = string.Empty;
            return true;
        }

        if (!string.Equals(mode, "custom", StringComparison.Ordinal))
        {
            request = null!;
            error = "CSV mode must be 'automatic' or 'custom'.";
            return false;
        }

        if (!TryBoolean(query, "header", out var header)
            || !TryBoolean(query, "ignoreErrors", out var ignoreErrors)
            || !TryBoolean(query, "storeRejects", out var storeRejects))
        {
            request = null!;
            error = "Header, ignoreErrors, and storeRejects must be true or false.";
            return false;
        }

        if (storeRejects && !ignoreErrors)
        {
            request = null!;
            error = "Reject reporting requires ignoreErrors to be true.";
            return false;
        }

        if (!long.TryParse(Value(query, "sampleSize"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sampleSize))
        {
            request = null!;
            error = "Sample size must be -1 for the full file or a positive number.";
            return false;
        }

        if (!TryNewLine(Value(query, "newLine"), out var newLine))
        {
            request = null!;
            error = "New line must be 'lf', 'cr', or 'crlf'.";
            return false;
        }

        request = new TabularImportForm(
            fileName,
            format,
            schema,
            table,
            AutomaticMode: false,
            Options: new CsvReadOptions(
                Value(query, "delimiter"),
                Value(query, "quote"),
                Value(query, "escape"),
                newLine,
                header,
                sampleSize,
                ignoreErrors,
                storeRejects),
            Worksheet: null);
        error = string.Empty;
        return true;
    }

    private static bool TryBoolean(IQueryCollection query, string key, out bool value)
        => bool.TryParse(Value(query, key), out value);

    private static bool TryNewLine(string value, out CsvNewLine newLine)
    {
        newLine = value switch
        {
            "lf" => CsvNewLine.Lf,
            "cr" => CsvNewLine.Cr,
            "crlf" => CsvNewLine.CrLf,
            _ => default,
        };
        return value is "lf" or "cr" or "crlf";
    }

    private static string Value(IQueryCollection query, string key) => query[key].ToString();

    private static bool TryFileFormat(string fileName, out TabularFileFormat format)
    {
        format = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".csv" => TabularFileFormat.Csv,
            ".xlsx" => TabularFileFormat.Xlsx,
            _ => default,
        };
        return Path.GetExtension(fileName).Equals(".csv", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(fileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedContentType(string? contentType, TabularFileFormat format)
    {
        if (contentType?.StartsWith(
                "application/octet-stream",
                StringComparison.OrdinalIgnoreCase) is true)
        {
            return true;
        }

        return format switch
        {
            TabularFileFormat.Csv =>
                contentType?.StartsWith("text/csv", StringComparison.OrdinalIgnoreCase) is true,
            TabularFileFormat.Xlsx =>
                contentType?.StartsWith(
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    StringComparison.OrdinalIgnoreCase) is true,
            _ => false,
        };
    }

    private sealed record TabularImportForm(
        string FileName,
        TabularFileFormat Format,
        string Schema,
        string Table,
        bool AutomaticMode,
        CsvReadOptions Options,
        string? Worksheet);
}
