using System.Diagnostics;
using System.Globalization;
using System.Text;
using Lakehold.Engine.Execution;

namespace Lakehold.Engine.Catalog;

/// <summary>The newline convention explicitly supplied to DuckDB's CSV reader.</summary>
public enum CsvNewLine
{
    Lf,
    Cr,
    CrLf,
}

/// <summary>
///     Optional overrides for DuckDB's CSV sniffer. Null properties are omitted so automatic mode
///     remains DuckDB's native <c>read_csv(path)</c> behavior.
/// </summary>
public sealed record CsvReadOptions(
    string? Delimiter = null,
    string? Quote = null,
    string? Escape = null,
    CsvNewLine? NewLine = null,
    bool? Header = null,
    long? SampleSize = null,
    bool? IgnoreErrors = null,
    bool? StoreRejects = null);

/// <summary>A column created from an uploaded CSV file.</summary>
public sealed record CsvImportedColumn(string Name, string DataType);

/// <summary>One faulty CSV line reported by DuckDB.</summary>
public sealed record CsvReject(
    long Line,
    string? ColumnName,
    string ErrorType,
    string CsvLine,
    string ErrorMessage);

/// <summary>The durable table and bounded reject report produced by one CSV import.</summary>
public sealed record CsvImportResult(
    string FileName,
    string Schema,
    string Table,
    long RowsImported,
    long RejectedRows,
    long RecordedErrors,
    bool RejectsTruncated,
    IReadOnlyList<CsvImportedColumn> Columns,
    IReadOnlyList<CsvReject> Rejects,
    TimeSpan Elapsed);

/// <summary>Creates one DuckLake table from an uploaded CSV file.</summary>
/// <remarks>
///     The file path is node-local disposable scratch state, but the entire operation runs inside
///     one request and one Duckling gate. Durable rows are committed to DuckLake before the caller
///     removes that file, so no later request, process, or node needs the scratch path.
/// </remarks>
public static class CsvImporter
{
    private const int RejectPreviewLimit = 100;
    private const int RejectCaptureLimit = RejectPreviewLimit + 1;
    private const int RejectTextLimit = 4096;

    /// <summary>Imports <paramref name="filePath"/> into a new table, refusing replacement.</summary>
    public static Task<CsvImportResult> ImportAsync(
        Duckling duckling,
        string filePath,
        string fileName,
        string schema,
        string table,
        CsvReadOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(options);

        var validatedSchema = SqlIdentifier.Quote(schema);
        var validatedTable = SqlIdentifier.Quote(table);
        ValidateOptions(options);

        return duckling.InvokeLabelledAsync(
            $"lakehold csv import: {validatedSchema}.{validatedTable}",
            ct => ImportUnguardedAsync(
                duckling,
                filePath,
                Path.GetFileName(fileName),
                validatedSchema,
                validatedTable,
                options,
                ct),
            cancellationToken);
    }

    private static async Task<CsvImportResult> ImportUnguardedAsync(
        Duckling duckling,
        string filePath,
        string fileName,
        string schema,
        string table,
        CsvReadOptions options,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var exists = await duckling
            .ExecuteUnguardedAsync(
                $"""
                 SELECT count(*)
                 FROM information_schema.tables
                 WHERE table_catalog = {SqlIdentifier.Literal(duckling.Catalog.CatalogName)}
                   AND table_schema = {SqlIdentifier.Literal(schema)}
                   AND table_name = {SqlIdentifier.Literal(table)}
                 """,
                cancellationToken)
            .ConfigureAwait(false);
        if (Count(exists.Rows.Single()[0]) > 0)
        {
            throw new ArgumentException(
                $"Table '{schema}.{table}' already exists. Choose a new table name; CSV import never replaces existing data.");
        }

        var suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var rejectScans = $"__lakehold_csv_scans_{suffix}";
        var rejectErrors = $"__lakehold_csv_errors_{suffix}";
        var storeRejects = options.StoreRejects is true;

        try
        {
            var target = $"{SqlIdentifier.QuoteName(schema)}.{SqlIdentifier.QuoteName(table)}";
            var reader = BuildReader(filePath, options, rejectScans, rejectErrors);
            await duckling
                .ExecuteUnguardedAsync($"CREATE TABLE {target} AS SELECT * FROM {reader}", cancellationToken)
                .ConfigureAwait(false);

            var description = await duckling
                .ExecuteUnguardedAsync($"DESCRIBE {target}", cancellationToken)
                .ConfigureAwait(false);
            var columns = description.Rows
                .Select(row => new CsvImportedColumn(Text(row[0]), Text(row[1])))
                .ToArray();

            var rowCount = await duckling
                .ExecuteUnguardedAsync($"SELECT count(*) FROM {target}", cancellationToken)
                .ConfigureAwait(false);

            if (!storeRejects)
            {
                return new CsvImportResult(
                    fileName,
                    schema,
                    table,
                    Count(rowCount.Rows.Single()[0]),
                    0,
                    0,
                    false,
                    columns,
                    [],
                    Stopwatch.GetElapsedTime(startedAt));
            }

            var quotedErrors = SqlIdentifier.QuoteName(rejectErrors);
            var rejectCounts = await duckling
                .ExecuteUnguardedAsync(
                    $"""
                     SELECT
                         (SELECT count(*) FROM {quotedErrors}) AS recorded_errors,
                         (SELECT count(*) FROM (
                             SELECT DISTINCT scan_id, file_id, line FROM {quotedErrors}
                         )) AS rejected_rows
                     """,
                    cancellationToken)
                .ConfigureAwait(false);
            var recordedErrors = Count(rejectCounts.Rows.Single()[0]);
            var rejectedRows = Count(rejectCounts.Rows.Single()[1]);

            var rejectResult = await duckling
                .ExecuteUnguardedAsync(
                    $"""
                     SELECT
                         line,
                         column_name,
                         CAST(error_type AS VARCHAR),
                         left(csv_line, {RejectTextLimit.ToString(CultureInfo.InvariantCulture)}),
                         left(error_message, {RejectTextLimit.ToString(CultureInfo.InvariantCulture)})
                     FROM {quotedErrors}
                     ORDER BY line, column_idx
                     LIMIT {RejectPreviewLimit.ToString(CultureInfo.InvariantCulture)}
                     """,
                    cancellationToken)
                .ConfigureAwait(false);
            var rejects = rejectResult.Rows
                .Select(row => new CsvReject(
                    Count(row[0]),
                    row[1] is null ? null : Text(row[1]),
                    Text(row[2]),
                    Text(row[3]),
                    Text(row[4])))
                .ToArray();

            return new CsvImportResult(
                fileName,
                schema,
                table,
                Count(rowCount.Rows.Single()[0]),
                rejectedRows,
                recordedErrors,
                recordedErrors > RejectPreviewLimit,
                columns,
                rejects,
                Stopwatch.GetElapsedTime(startedAt));
        }
        catch (DuckDB.NET.Data.DuckDBException ex)
        {
            // DuckDB includes uploaded row contents and the node-local file path in its parser
            // diagnostic. Translate at the engine boundary so neither durable query history,
            // telemetry, nor the HTTP response can retain that sensitive context.
            throw CsvImportException.FromDuckDb(ex.Message);
        }
        finally
        {
            if (storeRejects)
            {
                // Reject tables are useful only long enough to build the response. Unique names
                // prevent a warm session's previous import from contaminating this one.
                await DropTemporaryAsync(duckling, rejectErrors).ConfigureAwait(false);
                await DropTemporaryAsync(duckling, rejectScans).ConfigureAwait(false);
            }
        }
    }

    private static string BuildReader(
        string filePath,
        CsvReadOptions options,
        string rejectScans,
        string rejectErrors)
    {
        var arguments = new List<string> { SqlIdentifier.Literal(filePath) };
        AddString(arguments, "delim", options.Delimiter);
        AddString(arguments, "quote", options.Quote);
        AddString(arguments, "escape", options.Escape);

        if (options.NewLine is { } newLine)
        {
            arguments.Add($"new_line = {SqlIdentifier.Literal(newLine switch
            {
                CsvNewLine.Lf => "\\n",
                CsvNewLine.Cr => "\\r",
                CsvNewLine.CrLf => "\\r\\n",
                _ => throw new ArgumentOutOfRangeException(nameof(options)),
            })}");
        }

        if (options.Header is { } header)
        {
            arguments.Add($"header = {Boolean(header)}");
        }

        if (options.SampleSize is { } sampleSize)
        {
            arguments.Add($"sample_size = {sampleSize.ToString(CultureInfo.InvariantCulture)}");
        }

        if (options.IgnoreErrors is { } ignoreErrors)
        {
            arguments.Add($"ignore_errors = {Boolean(ignoreErrors)}");
        }

        if (options.StoreRejects is { } storeRejects)
        {
            arguments.Add($"store_rejects = {Boolean(storeRejects)}");
            if (storeRejects)
            {
                arguments.Add($"rejects_scan = {SqlIdentifier.Literal(rejectScans)}");
                arguments.Add($"rejects_table = {SqlIdentifier.Literal(rejectErrors)}");
                // Capture one sentinel beyond the response limit so the caller can distinguish
                // exactly 100 errors from a truncated report without retaining an unbounded table.
                arguments.Add(
                    $"rejects_limit = {RejectCaptureLimit.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        var sql = new StringBuilder("read_csv(\n    ");
        sql.AppendJoin(",\n    ", arguments);
        sql.Append("\n)");
        return sql.ToString();
    }

    private static void ValidateOptions(CsvReadOptions options)
    {
        ValidateCsvToken(options.Delimiter, "delimiter", allowEmpty: false);
        ValidateCsvToken(options.Quote, "quote", allowEmpty: true);
        ValidateCsvToken(options.Escape, "escape", allowEmpty: true);

        if (options.SampleSize is { } sampleSize && sampleSize is not -1 && sampleSize <= 0)
        {
            throw new ArgumentException("CSV sample size must be -1 for the full file or a positive number.");
        }

        if (options.StoreRejects is true && options.IgnoreErrors is false)
        {
            throw new ArgumentException(
                "CSV reject reporting requires malformed rows to be skipped. "
                + "Set ignoreErrors to true or disable storeRejects.");
        }
    }

    private static void ValidateCsvToken(string? value, string name, bool allowEmpty)
    {
        if (value is null)
        {
            return;
        }

        if ((!allowEmpty && value.Length == 0) || Encoding.UTF8.GetByteCount(value) > 4)
        {
            throw new ArgumentException(
                $"CSV {name} must be {(allowEmpty ? "empty or " : string.Empty)}one character up to four UTF-8 bytes.");
        }
    }

    private static void AddString(List<string> arguments, string name, string? value)
    {
        if (value is not null)
        {
            arguments.Add($"{name} = {SqlIdentifier.Literal(value)}");
        }
    }

    private static async Task DropTemporaryAsync(Duckling duckling, string name)
    {
        try
        {
            await duckling
                .ExecuteUnguardedAsync(
                    $"DROP TABLE IF EXISTS {SqlIdentifier.QuoteName(name)}",
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (DuckDB.NET.Data.DuckDBException)
        {
            // A failed scan may roll the table creation back before cleanup reaches it.
        }
    }

    private static string Boolean(bool value) => value ? "true" : "false";

    private static long Count(object? value)
        => value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private static string Text(object? value)
        => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}
