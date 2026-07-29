using System.Globalization;
using Lakehold.Api.Auth;
using Lakehold.Api.Importing;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Security;
using Lakehold.Engine.Catalog;
using Microsoft.AspNetCore.Mvc;

namespace Lakehold.Api.Endpoints;

/// <summary>Browser upload and CSV-to-table endpoint.</summary>
public static class CsvImportEndpoints
{
    /// <summary>Adds the catalog-scoped CSV import route.</summary>
    public static void MapCsvImportEndpoints(this RouteGroupBuilder tenants, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        tenants.MapPost("/{tenantSlug}/catalogs/{catalogName}/imports/csv", ImportAsync)
            .RequireCapability(Capability.TenantWrite)
            .WithMetadata(new RequestSizeLimitAttribute(maxBytes))
            .WithSummary("Uploads a CSV file and creates a new DuckLake table.");
    }

    internal static async Task<IResult> ImportAsync(
        HttpContext http,
        string tenantSlug,
        string catalogName,
        CsvUploadService uploads,
        CancellationToken cancellationToken)
    {
        if (http.Request.ContentType is null
            || (!http.Request.ContentType.StartsWith("text/csv", StringComparison.OrdinalIgnoreCase)
                && !http.Request.ContentType.StartsWith(
                    "application/octet-stream",
                    StringComparison.OrdinalIgnoreCase)))
        {
            return Results.BadRequest("CSV import requires a text/csv or application/octet-stream body.");
        }

        if (!TryReadRequest(http.Request.Query, out var request, out var validationError))
        {
            return Results.BadRequest(validationError);
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
                    request.Schema,
                    request.Table,
                    request.Options,
                    principal.TokenId,
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(CsvImportDto.From(result));
        }
        catch (CsvUploadTooLargeException ex)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "CSV upload rejected",
                detail: ex.Message);
        }
        catch (CsvScratchCapacityException ex)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status507InsufficientStorage,
                title: "CSV scratch capacity unavailable",
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
        catch (DuckDB.NET.Data.DuckDBException ex)
        {
            return Results.BadRequest(ex.Message);
        }
    }

    private static bool TryReadRequest(
        IQueryCollection query,
        out CsvImportForm request,
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

        var mode = Value(query, "mode");
        if (string.IsNullOrEmpty(mode) || string.Equals(mode, "automatic", StringComparison.Ordinal))
        {
            request = new CsvImportForm(fileName, schema, table, new CsvReadOptions());
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

        request = new CsvImportForm(
            fileName,
            schema,
            table,
            new CsvReadOptions(
                Value(query, "delimiter"),
                Value(query, "quote"),
                Value(query, "escape"),
                newLine,
                header,
                sampleSize,
                ignoreErrors,
                storeRejects));
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

    private sealed record CsvImportForm(
        string FileName,
        string Schema,
        string Table,
        CsvReadOptions Options);
}
