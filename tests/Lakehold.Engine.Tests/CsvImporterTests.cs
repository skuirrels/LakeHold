using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Engine.Tests;

/// <summary>Covers automatic CSV ingestion and the production-export dialect used by the UI.</summary>
public sealed class CsvImporterTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lakehold-csv-tests", Guid.NewGuid().ToString("N"));
    private DucklingPool _pool = null!;
    private Duckling _duckling = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var options = new LakehouseOptions
        {
            DataRoot = Path.Combine(_root, "data"),
            MetadataRoot = Path.Combine(_root, "catalogs"),
        };
        Directory.CreateDirectory(options.DataRoot);
        Directory.CreateDirectory(options.MetadataRoot);

        _pool = new DucklingPool(Options.Create(options), NullLoggerFactory.Instance);
        _duckling = await _pool.GetOrStartAsync(
            new CatalogDescriptor(
                "csvlake",
                CatalogMetadataKind.LocalFile,
                Path.Combine(options.MetadataRoot, "csv.ducklake"),
                options.DataRoot),
            configure: null,
            CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _pool.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failing must not fail the test run.
        }
    }

    [Fact]
    public async Task Automatic_mode_uses_DuckDB_sniffing_and_creates_a_table()
    {
        var path = Path.Combine(_root, "customers.csv");
        await File.WriteAllTextAsync(path, "id,name,active\n1,Alice,true\n2,Bob,false\n");

        var result = await CsvImporter.ImportAsync(
            _duckling,
            path,
            "customers.csv",
            "main",
            "customers",
            new CsvReadOptions(),
            default);

        Assert.Equal(2, result.RowsImported);
        Assert.Equal(0, result.RejectedRows);
        Assert.Collection(
            result.Columns,
            column =>
            {
                Assert.Equal("id", column.Name);
                Assert.Equal("BIGINT", column.DataType);
            },
            column =>
            {
                Assert.Equal("name", column.Name);
                Assert.Equal("VARCHAR", column.DataType);
            },
            column =>
            {
                Assert.Equal("active", column.Name);
                Assert.Equal("BOOLEAN", column.DataType);
            });

        var rows = await Sql("SELECT id, name, active FROM customers ORDER BY id");
        Assert.Equal(2, rows.Rows.Count);
        Assert.Equal("Alice", rows.Rows[0][1]);
    }

    [Fact]
    public async Task Custom_mode_replicates_semicolon_crlf_full_scan_and_returns_rejects()
    {
        var path = Path.Combine(_root, "schedules.csv");
        await File.WriteAllTextAsync(
            path,
            "id;name\r\n1;\"First\"\r\n2\r\n3;\"Third\"\r\n");

        var result = await CsvImporter.ImportAsync(
            _duckling,
            path,
            "sch_predicted_schedules.csv",
            "main",
            "predicted_schedules",
            new CsvReadOptions(
                Delimiter: ";",
                Quote: "\"",
                Escape: "",
                NewLine: CsvNewLine.CrLf,
                Header: true,
                SampleSize: -1,
                IgnoreErrors: true,
                StoreRejects: true),
            default);

        Assert.Equal(2, result.RowsImported);
        Assert.Equal(1, result.RejectedRows);
        Assert.True(result.RecordedErrors >= 1);
        var reject = Assert.Single(result.Rejects);
        Assert.Equal(3, reject.Line);
        Assert.Contains("2", reject.CsvLine, StringComparison.Ordinal);

        var rows = await Sql("SELECT id, name FROM predicted_schedules ORDER BY id");
        Assert.Equal(["1", "First"], rows.Rows[0].Select(Convert.ToString));
        Assert.Equal(["3", "Third"], rows.Rows[1].Select(Convert.ToString));

        // Reject staging belongs to the response, not to the warm catalog session.
        var temporaryTables = await Sql(
            "SELECT count(*) FROM information_schema.tables WHERE table_name LIKE '__lakehold_csv_%'");
        Assert.Equal(0, Convert.ToInt64(temporaryTables.Rows.Single()[0]));
    }

    [Fact]
    public void Duckdb_csv_diagnostic_never_returns_the_uploaded_row_or_scratch_path()
    {
        const string diagnostic =
            """
            Invalid Input Error: CSV Error on Line: 904218
            Original Line: customer;secret-value
            Expected Number of Columns: 157 Found: 135
            file = /tmp/lakehold-csv-imports/private.csv
            """;

        var failure = CsvImportException.FromDuckDb(diagnostic);

        Assert.True(failure.IsParserError);
        Assert.Equal(CsvImportException.ParserErrorCode, failure.Code);
        Assert.Equal(
            "CSV line 904218 contains 135 columns; 157 were expected.",
            failure.Message);
        Assert.DoesNotContain("secret-value", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generic_duckdb_failure_never_returns_the_uploaded_path()
    {
        var failure = CsvImportException.FromDuckDb(
            "IO Error: Cannot open file '/tmp/lakehold-csv-imports/private.csv'");

        Assert.False(failure.IsParserError);
        Assert.Equal(CsvImportException.ImportErrorCode, failure.Code);
        Assert.Equal(
            "DuckDB could not import the CSV with the selected settings.",
            failure.Message);
        Assert.DoesNotContain("/tmp/", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Existing_table_is_refused_without_changing_it()
    {
        var path = Path.Combine(_root, "existing.csv");
        await File.WriteAllTextAsync(path, "id\n2\n");
        await Sql("CREATE TABLE existing (id BIGINT)");
        await Sql("INSERT INTO existing VALUES (1)");

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => CsvImporter.ImportAsync(
                _duckling,
                path,
                "existing.csv",
                "main",
                "existing",
                new CsvReadOptions(),
                default));

        Assert.Contains("already exists", error.Message, StringComparison.Ordinal);
        var rows = await Sql("SELECT id FROM existing");
        Assert.Single(rows.Rows);
        Assert.Equal(1L, rows.Rows[0][0]);
    }

    [Fact]
    public async Task Untrusted_identifiers_and_invalid_dialect_values_are_refused()
    {
        var path = Path.Combine(_root, "safe.csv");
        await File.WriteAllTextAsync(path, "id\n1\n");

        await Assert.ThrowsAsync<ArgumentException>(
            () => CsvImporter.ImportAsync(
                _duckling,
                path,
                "safe.csv",
                "main",
                "target; DROP TABLE other",
                new CsvReadOptions(),
                default));

        await Assert.ThrowsAsync<ArgumentException>(
            () => CsvImporter.ImportAsync(
                _duckling,
                path,
                "safe.csv",
                "main",
                "safe",
                new CsvReadOptions(Delimiter: "too-long"),
                default));

        var incompatible = await Assert.ThrowsAsync<ArgumentException>(
            () => CsvImporter.ImportAsync(
                _duckling,
                path,
                "safe.csv",
                "main",
                "safe",
                new CsvReadOptions(IgnoreErrors: false, StoreRejects: true),
                default));
        Assert.Contains("requires malformed rows to be skipped", incompatible.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reject_capture_is_bounded_and_reports_that_the_preview_is_truncated()
    {
        var path = Path.Combine(_root, "many-rejects.csv");
        var csv = new System.Text.StringBuilder("id;name\r\n1;valid\r\n");
        for (var index = 0; index < 105; index++)
        {
            csv.Append(index + 2).Append("\r\n");
        }

        await File.WriteAllTextAsync(path, csv.ToString());

        var result = await CsvImporter.ImportAsync(
            _duckling,
            path,
            "many-rejects.csv",
            "main",
            "bounded_rejects",
            new CsvReadOptions(
                Delimiter: ";",
                NewLine: CsvNewLine.CrLf,
                Header: true,
                SampleSize: -1,
                IgnoreErrors: true,
                StoreRejects: true),
            default);

        Assert.Equal(1, result.RowsImported);
        Assert.Equal(101, result.RecordedErrors);
        Assert.Equal(100, result.Rejects.Count);
        Assert.True(result.RejectsTruncated);
    }

    private Task<QueryResult> Sql(string sql)
        => _duckling.ExecuteQueryAsync(sql, CancellationToken.None);
}
