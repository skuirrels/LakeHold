using System.Diagnostics;
using System.Globalization;
using System.Text;
using Lakehold.Engine.Execution;

namespace Lakehold.Engine.Catalog;

/// <summary>Browser-uploaded tabular formats supported by LakeHold.</summary>
public enum TabularFileFormat
{
    Csv,
    Xlsx,
}

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

/// <summary>A column created from an uploaded tabular file.</summary>
public sealed record TabularImportedColumn(string Name, string DataType);

/// <summary>One faulty CSV line reported by DuckDB.</summary>
public sealed record CsvReject(
    long Line,
    string? ColumnName,
    string ErrorType,
    string CsvLine,
    string ErrorMessage);

/// <summary>The durable table and bounded reject report produced by one tabular-file import.</summary>
public sealed record TabularImportResult(
    string FileName,
    TabularFileFormat Format,
    string Schema,
    string Table,
    long RowsImported,
    long RejectedRows,
    long RecordedErrors,
    bool RejectsTruncated,
    bool UsedAutomaticFallback,
    IReadOnlyList<TabularImportedColumn> Columns,
    IReadOnlyList<CsvReject> Rejects,
    TimeSpan Elapsed);

/// <summary>Creates one DuckLake table from an uploaded CSV or XLSX file.</summary>
/// <remarks>
///     The file path is node-local disposable scratch state, but the entire operation runs inside
///     one request and one Duckling gate. Durable rows are committed to DuckLake before the caller
///     removes that file, so no later request, process, or node needs the scratch path.
/// </remarks>
public static class TabularImporter
{
    private const int RejectPreviewLimit = 100;
    private const int RejectCaptureLimit = RejectPreviewLimit + 1;
    private const int RejectTextLimit = 4096;

    /// <summary>Imports a CSV file into a new table, refusing replacement.</summary>
    public static Task<TabularImportResult> ImportCsvAsync(
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

        return ImportAsync(
            duckling,
            filePath,
            fileName,
            validatedSchema,
            validatedTable,
            TabularFileFormat.Csv,
            $"lakehold csv import: {validatedSchema}.{validatedTable}",
            options.StoreRejects is true,
            (path, scans, errors) => BuildCsvReader(path, options, scans, errors),
            cancellationToken);
    }

    /// <summary>Imports one worksheet from an XLSX workbook into a new table.</summary>
    /// <param name="sheet">Worksheet name, or null to use the first worksheet.</param>
    public static Task<TabularImportResult> ImportXlsxAsync(
        Duckling duckling,
        string filePath,
        string fileName,
        string schema,
        string table,
        string? sheet,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var validatedSchema = SqlIdentifier.Quote(schema);
        var validatedTable = SqlIdentifier.Quote(table);
        var normalizedSheet = string.IsNullOrWhiteSpace(sheet) ? null : sheet.Trim();
        if (normalizedSheet?.Length > 255)
        {
            throw new ArgumentException("XLSX worksheet names cannot exceed 255 characters.", nameof(sheet));
        }

        return ImportAsync(
            duckling,
            filePath,
            fileName,
            validatedSchema,
            validatedTable,
            TabularFileFormat.Xlsx,
            $"lakehold xlsx import: {validatedSchema}.{validatedTable}",
            storeRejects: false,
            (path, _, _) => BuildXlsxReader(path, normalizedSheet),
            cancellationToken);
    }

    private static Task<TabularImportResult> ImportAsync(
        Duckling duckling,
        string filePath,
        string fileName,
        string schema,
        string table,
        TabularFileFormat format,
        string commitMessage,
        bool storeRejects,
        Func<string, string, string, string> buildReader,
        CancellationToken cancellationToken)
        => duckling.InvokeLabelledAsync(
            commitMessage,
            ct => ImportUnguardedAsync(
                duckling,
                filePath,
                Path.GetFileName(fileName),
                format,
                schema,
                table,
                storeRejects,
                buildReader,
                ct),
            cancellationToken);

    private static async Task<TabularImportResult> ImportUnguardedAsync(
        Duckling duckling,
        string filePath,
        string fileName,
        TabularFileFormat format,
        string schema,
        string table,
        bool storeRejects,
        Func<string, string, string, string> buildReader,
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
                $"Table '{schema}.{table}' already exists. Choose a new table name; file import never replaces existing data.");
        }

        var suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var rejectScans = $"__lakehold_csv_scans_{suffix}";
        var rejectErrors = $"__lakehold_csv_errors_{suffix}";
        var importCompleted = false;
        try
        {
            var target = $"{SqlIdentifier.QuoteName(schema)}.{SqlIdentifier.QuoteName(table)}";
            var reader = buildReader(filePath, rejectScans, rejectErrors);
            await duckling
                .ExecuteUnguardedAsync($"CREATE TABLE {target} AS SELECT * FROM {reader}", cancellationToken)
                .ConfigureAwait(false);

            var description = await duckling
                .ExecuteUnguardedAsync($"DESCRIBE {target}", cancellationToken)
                .ConfigureAwait(false);
            var columns = description.Rows
                .Select(row => new TabularImportedColumn(Text(row[0]), Text(row[1])))
                .ToArray();

            var rowCount = await duckling
                .ExecuteUnguardedAsync($"SELECT count(*) FROM {target}", cancellationToken)
                .ConfigureAwait(false);

            if (!storeRejects)
            {
                importCompleted = true;
                return new TabularImportResult(
                    fileName,
                    format,
                    schema,
                    table,
                    Count(rowCount.Rows.Single()[0]),
                    0,
                    0,
                    false,
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

            importCompleted = true;
            return new TabularImportResult(
                fileName,
                format,
                schema,
                table,
                Count(rowCount.Rows.Single()[0]),
                rejectedRows,
                recordedErrors,
                recordedErrors > RejectPreviewLimit,
                false,
                columns,
                rejects,
                Stopwatch.GetElapsedTime(startedAt));
        }
        catch (DuckDB.NET.Data.DuckDBException ex)
        {
            // DuckDB includes uploaded row contents and the node-local file path in its parser
            // diagnostic. Translate at the engine boundary so neither durable query history,
            // telemetry, nor the HTTP response can retain that sensitive context.
            if (format == TabularFileFormat.Csv)
            {
                throw CsvImportException.FromDuckDb(ex.Message);
            }

            throw XlsxImportException.FromDuckDb(ex.Message);
        }
        finally
        {
            if (storeRejects)
            {
                // Reject tables are useful only long enough to build the response. Unique names
                // prevent a warm session's previous import from contaminating this one. A parser
                // failure has already invalidated the transaction and its eventual rollback removes
                // the tables, so cleanup errors may be suppressed only on that failed path. Once the
                // import itself succeeds, cleanup must succeed too or the transaction is rolled back
                // rather than retaining uploaded rows in the warm session.
                await DropTemporaryAsync(duckling, rejectErrors, importCompleted).ConfigureAwait(false);
                await DropTemporaryAsync(duckling, rejectScans, importCompleted).ConfigureAwait(false);
            }
        }
    }

    private static string BuildCsvReader(
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

    private static string BuildXlsxReader(string filePath, string? sheet)
    {
        var arguments = new List<string> { SqlIdentifier.Literal(filePath) };
        AddString(arguments, "sheet", sheet);

        var sql = new StringBuilder("read_xlsx(\n    ");
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

    private static async Task DropTemporaryAsync(
        Duckling duckling,
        string name,
        bool cleanupRequired)
    {
        try
        {
            await duckling
                .ExecuteUnguardedAsync(
                    $"DROP TABLE IF EXISTS {SqlIdentifier.QuoteName(name)}",
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (DuckDB.NET.Data.DuckDBException) when (!cleanupRequired)
        {
            // A failed scan may abort the transaction before cleanup reaches it. The enclosing
            // transaction is disposed without commit, so its rollback owns removal on this path.
        }
        catch (DuckDB.NET.Data.DuckDBException ex)
        {
            // Do not allow a nominally successful import to commit while raw reject data remains in
            // the session. Translate the diagnostic so the scratch path or row contents cannot reach
            // durable audit history.
            throw CsvImportException.FromDuckDb(ex.Message);
        }
    }

    private static string Boolean(bool value) => value ? "true" : "false";

    private static long Count(object? value)
        => value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private static string Text(object? value)
        => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}
