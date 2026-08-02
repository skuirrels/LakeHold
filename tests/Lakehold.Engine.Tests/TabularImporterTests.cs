using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Engine.Tests;

/// <summary>Covers automatic CSV/XLSX ingestion and the production-export dialect used by the UI.</summary>
public sealed class TabularImporterTests : IAsyncLifetime
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

        var result = await TabularImporter.ImportCsvAsync(
            _duckling,
            path,
            "customers.csv",
            "main",
            "customers",
            new CsvReadOptions(),
            default);

        Assert.Equal(2, result.RowsImported);
        Assert.Equal(TabularFileFormat.Csv, result.Format);
        Assert.False(result.UsedAutomaticFallback);
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

        var result = await TabularImporter.ImportCsvAsync(
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
    public void Csv_sniffer_failure_is_safe_and_eligible_for_automatic_recovery()
    {
        var failure = CsvImportException.FromDuckDb(
            "Error when sniffing file \"/tmp/private.csv\". Original Line: customer;secret");

        Assert.True(failure.IsParserError);
        Assert.Equal(CsvImportException.ParserErrorCode, failure.Code);
        Assert.Equal(
            "DuckDB could not parse the CSV with the selected settings.",
            failure.Message);
        Assert.DoesNotContain("secret", failure.Message, StringComparison.Ordinal);
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
    public void Xlsx_failure_never_returns_workbook_cells_or_the_scratch_path()
    {
        var failure = XlsxImportException.FromDuckDb(
            "Invalid Input Error: secret-cell-value in /tmp/lakehold-csv-imports/private.xlsx");

        Assert.Equal(XlsxImportException.ImportErrorCode, failure.Code);
        Assert.DoesNotContain("secret-cell-value", failure.Message, StringComparison.Ordinal);
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
            () => TabularImporter.ImportCsvAsync(
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
            () => TabularImporter.ImportCsvAsync(
                _duckling,
                path,
                "safe.csv",
                "main",
                "target; DROP TABLE other",
                new CsvReadOptions(),
                default));

        await Assert.ThrowsAsync<ArgumentException>(
            () => TabularImporter.ImportCsvAsync(
                _duckling,
                path,
                "safe.csv",
                "main",
                "safe",
                new CsvReadOptions(Delimiter: "too-long"),
                default));

        var incompatible = await Assert.ThrowsAsync<ArgumentException>(
            () => TabularImporter.ImportCsvAsync(
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

        var result = await TabularImporter.ImportCsvAsync(
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

    [Fact]
    public async Task Xlsx_import_uses_the_first_worksheet_and_infers_columns()
    {
        var path = Path.Combine(_root, "customers.xlsx");
        await Sql(
            $"""
             COPY (
                 SELECT *
                 FROM (VALUES (1, 'Alice'), (2, 'Bob')) AS customers(id, name)
             ) TO {SqlIdentifier.Literal(path)} WITH (FORMAT xlsx, HEADER true)
             """);

        var result = await TabularImporter.ImportXlsxAsync(
            _duckling,
            path,
            "customers.xlsx",
            "main",
            "xlsx_customers",
            sheet: null,
            default);

        Assert.Equal(TabularFileFormat.Xlsx, result.Format);
        Assert.Equal(2, result.RowsImported);
        Assert.False(result.UsedAutomaticFallback);
        Assert.Equal(["id", "name"], result.Columns.Select(column => column.Name));
        Assert.Empty(result.Rejects);

        var rows = await Sql("SELECT id, name FROM xlsx_customers ORDER BY id");
        Assert.Equal(2, rows.Rows.Count);
        Assert.Equal("Alice", rows.Rows[0][1]);
    }

    [Fact]
    public async Task Connector_snapshot_replaces_the_target_only_after_quality_passes()
    {
        var initial = Path.Combine(_root, "connector-initial.ndjson");
        await File.WriteAllTextAsync(initial, "{\"id\":1,\"name\":\"old\"}\n");
        var first = await JsonSnapshotImporter.ReplaceAsync(
            _duckling,
            initial,
            "main",
            "connector_customers",
            replaceExistingTarget: false,
            new JsonSnapshotQualityPolicy(1, ["id", "name"], ["id"]),
            default);
        Assert.Equal(1, first.RowsPublished);

        var replacement = Path.Combine(_root, "connector-replacement.ndjson");
        await File.WriteAllTextAsync(
            replacement,
            "{\"id\":2,\"name\":\"Alice\"}\n{\"id\":3,\"name\":\"Bob\"}\n");
        var second = await JsonSnapshotImporter.ReplaceAsync(
            _duckling,
            replacement,
            "main",
            "connector_customers",
            replaceExistingTarget: true,
            new JsonSnapshotQualityPolicy(2, ["id", "name"], ["id", "name"]),
            default);

        Assert.Equal(2, second.RowsPublished);
        var rows = await Sql("SELECT id, name FROM connector_customers ORDER BY id");
        Assert.Equal(2, rows.Rows.Count);
        Assert.Equal("Alice", rows.Rows[0][1]);
    }

    [Fact]
    public async Task Failed_connector_quality_gate_preserves_the_previous_snapshot()
    {
        var initial = Path.Combine(_root, "quality-initial.ndjson");
        await File.WriteAllTextAsync(initial, "{\"id\":1,\"name\":\"kept\"}\n");
        await JsonSnapshotImporter.ReplaceAsync(
            _duckling,
            initial,
            "main",
            "quality_target",
            replaceExistingTarget: false,
            new JsonSnapshotQualityPolicy(1, ["id", "name"], ["id"]),
            default);

        var invalid = Path.Combine(_root, "quality-invalid.ndjson");
        await File.WriteAllTextAsync(invalid, "{\"id\":2}\n");
        var failure = await Assert.ThrowsAsync<JsonSnapshotQualityException>(() =>
            JsonSnapshotImporter.ReplaceAsync(
                _duckling,
                invalid,
                "main",
                "quality_target",
                replaceExistingTarget: true,
                new JsonSnapshotQualityPolicy(1, ["id", "name"], ["id"]),
                default));
        Assert.Contains("missing required columns", failure.Message, StringComparison.Ordinal);

        var rows = await Sql("SELECT id, name FROM quality_target");
        var row = Assert.Single(rows.Rows);
        Assert.Equal(1L, row[0]);
        Assert.Equal("kept", row[1]);
    }

    [Fact]
    public async Task First_connector_publication_refuses_to_take_over_an_existing_table()
    {
        await Sql("CREATE TABLE existing_target (id BIGINT, source VARCHAR)");
        await Sql("INSERT INTO existing_target VALUES (41, 'unmanaged')");
        var snapshot = Path.Combine(_root, "ownership-conflict.ndjson");
        await File.WriteAllTextAsync(snapshot, "{\"id\":42,\"source\":\"connector\"}\n");

        var failure = await Assert.ThrowsAsync<JsonSnapshotTargetConflictException>(() =>
            JsonSnapshotImporter.ReplaceAsync(
                _duckling,
                snapshot,
                "main",
                "existing_target",
                replaceExistingTarget: false,
                new JsonSnapshotQualityPolicy(1, ["id", "source"], ["id"]),
                default));

        Assert.Contains("not owned", failure.Message, StringComparison.Ordinal);
        var rows = await Sql("SELECT id, source FROM existing_target");
        var row = Assert.Single(rows.Rows);
        Assert.Equal(41L, row[0]);
        Assert.Equal("unmanaged", row[1]);
    }

    [Fact]
    public async Task First_full_snapshot_replay_still_enforces_schema_policy()
    {
        var initial = Path.Combine(_root, "full-replay-initial.ndjson");
        await File.WriteAllTextAsync(initial, "{\"id\":1,\"name\":\"kept\"}\n");
        const string marker = "lakehold.connector:72";
        await JsonSnapshotImporter.ReplaceAsync(
            _duckling,
            initial,
            "main",
            "full_snapshot_replay",
            replaceExistingTarget: false,
            new JsonSnapshotQualityPolicy(1, ["id", "name"], ["id"]),
            DataConnectorSchemaBehavior.Reject,
            marker,
            default);

        var evolvedReplay = Path.Combine(_root, "full-replay-evolved.ndjson");
        await File.WriteAllTextAsync(
            evolvedReplay,
            "{\"id\":2,\"name\":\"replacement\",\"unexpected\":true}\n");
        var failure = await Assert.ThrowsAsync<JsonSnapshotQualityException>(() =>
            JsonSnapshotImporter.ReplaceAsync(
                _duckling,
                evolvedReplay,
                "main",
                "full_snapshot_replay",
                replaceExistingTarget: false,
                new JsonSnapshotQualityPolicy(1, ["id", "name"], ["id"]),
                DataConnectorSchemaBehavior.Reject,
                marker,
                default));

        Assert.Contains("adds columns", failure.Message, StringComparison.Ordinal);
        var row = Assert.Single((await Sql("SELECT id, name FROM full_snapshot_replay")).Rows);
        Assert.Equal(1L, row[0]);
        Assert.Equal("kept", row[1]);
    }

    [Fact]
    public async Task Incremental_connector_replay_upserts_by_key_without_duplicates()
    {
        var firstDelta = Path.Combine(_root, "incremental-first.ndjson");
        await File.WriteAllTextAsync(firstDelta, "{\"id\":1,\"name\":\"first\"}\n");
        await JsonSnapshotImporter.UpsertAsync(
            _duckling,
            firstDelta,
            "main",
            "incremental_customers",
            targetProvisioned: false,
            ["id"],
            new JsonSnapshotQualityPolicy(0, ["id", "name"], ["id"]),
            DataConnectorSchemaBehavior.Reject,
            default);

        var duplicateDelta = Path.Combine(_root, "incremental-duplicate.ndjson");
        await File.WriteAllTextAsync(
            duplicateDelta,
            "{\"id\":3,\"name\":\"duplicate-a\"}\n{\"id\":3,\"name\":\"duplicate-b\"}\n");
        var duplicateFailure = await Assert.ThrowsAsync<JsonSnapshotQualityException>(() =>
            JsonSnapshotImporter.UpsertAsync(
                _duckling,
                duplicateDelta,
                "main",
                "incremental_customers",
                targetProvisioned: true,
                ["id"],
                new JsonSnapshotQualityPolicy(0, ["id", "name"], ["id"]),
                DataConnectorSchemaBehavior.Reject,
                default));
        Assert.Contains("duplicate incremental keys", duplicateFailure.Message, StringComparison.Ordinal);

        var replayedDelta = Path.Combine(_root, "incremental-replay.ndjson");
        await File.WriteAllTextAsync(
            replayedDelta,
            "{\"id\":1,\"name\":\"updated\"}\n{\"id\":2,\"name\":\"second\"}\n");
        await JsonSnapshotImporter.UpsertAsync(
            _duckling,
            replayedDelta,
            "main",
            "incremental_customers",
            targetProvisioned: true,
            ["id"],
            new JsonSnapshotQualityPolicy(0, ["id", "name"], ["id"]),
            DataConnectorSchemaBehavior.Reject,
            default);
        await JsonSnapshotImporter.UpsertAsync(
            _duckling,
            replayedDelta,
            "main",
            "incremental_customers",
            targetProvisioned: true,
            ["id"],
            new JsonSnapshotQualityPolicy(0, ["id", "name"], ["id"]),
            DataConnectorSchemaBehavior.Reject,
            default);

        var rows = await Sql("SELECT id, name FROM incremental_customers ORDER BY id");
        Assert.Equal(2, rows.Rows.Count);
        Assert.Equal("updated", rows.Rows[0][1]);
        Assert.Equal("second", rows.Rows[1][1]);
    }

    [Fact]
    public async Task First_incremental_publication_replay_recognizes_its_durable_ownership_marker()
    {
        var delta = Path.Combine(_root, "incremental-first-replay.ndjson");
        await File.WriteAllTextAsync(delta, "{\"id\":1,\"name\":\"first\"}\n");
        const string marker = "lakehold.connector:71";
        await JsonSnapshotImporter.UpsertAsync(
            _duckling,
            delta,
            "main",
            "incremental_first_replay",
            targetProvisioned: false,
            ["id"],
            new JsonSnapshotQualityPolicy(0, ["id", "name"], ["id"]),
            DataConnectorSchemaBehavior.Reject,
            marker,
            default);

        await JsonSnapshotImporter.UpsertAsync(
            _duckling,
            delta,
            "main",
            "incremental_first_replay",
            targetProvisioned: false,
            ["id"],
            new JsonSnapshotQualityPolicy(0, ["id", "name"], ["id"]),
            DataConnectorSchemaBehavior.Reject,
            marker,
            default);

        Assert.Single((await Sql("SELECT id FROM incremental_first_replay")).Rows);
    }

    [Fact]
    public async Task Incremental_schema_policy_rejects_or_applies_only_additive_columns()
    {
        var initial = Path.Combine(_root, "schema-initial.ndjson");
        await File.WriteAllTextAsync(initial, "{\"id\":1,\"name\":\"first\"}\n");
        await JsonSnapshotImporter.UpsertAsync(
            _duckling,
            initial,
            "main",
            "schema_customers",
            targetProvisioned: false,
            ["id"],
            new JsonSnapshotQualityPolicy(0, ["id"], ["id"]),
            DataConnectorSchemaBehavior.Reject,
            default);

        var evolved = Path.Combine(_root, "schema-evolved.ndjson");
        await File.WriteAllTextAsync(evolved, "{\"id\":2,\"name\":\"second\",\"region\":\"eu\"}\n");
        var rejected = await Assert.ThrowsAsync<JsonSnapshotQualityException>(() =>
            JsonSnapshotImporter.UpsertAsync(
                _duckling,
                evolved,
                "main",
                "schema_customers",
                targetProvisioned: true,
                ["id"],
                new JsonSnapshotQualityPolicy(0, ["id"], ["id"]),
                DataConnectorSchemaBehavior.Reject,
                default));
        Assert.Contains("adds columns", rejected.Message, StringComparison.Ordinal);

        await JsonSnapshotImporter.UpsertAsync(
            _duckling,
            evolved,
            "main",
            "schema_customers",
            targetProvisioned: true,
            ["id"],
            new JsonSnapshotQualityPolicy(0, ["id"], ["id"]),
            DataConnectorSchemaBehavior.Additive,
            default);
        var rows = await Sql("SELECT id, name, region FROM schema_customers ORDER BY id");
        Assert.Equal(2, rows.Rows.Count);
        Assert.Null(rows.Rows[0][2]);
        Assert.Equal("eu", rows.Rows[1][2]);
    }

    private Task<QueryResult> Sql(string sql)
        => _duckling.ExecuteQueryAsync(sql, CancellationToken.None);
}
