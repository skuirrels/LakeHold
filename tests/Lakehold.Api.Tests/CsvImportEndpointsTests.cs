using Lakehold.Api.Endpoints;
using Lakehold.Api.Importing;
using Lakehold.ControlPlane.Data;
using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>Covers streamed bodies, scratch limits, and the API-to-engine CSV import path.</summary>
public sealed class CsvImportEndpointsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lakehold-csv-api-tests", Guid.NewGuid().ToString("N"));
    private ControlPlaneContext _context = null!;
    private DucklingPool _pool = null!;
    private CsvScratchSpace _scratch = null!;
    private CsvUploadService _uploads = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var lakehouseOptions = Options.Create(new LakehouseOptions
        {
            MetadataRoot = Path.Combine(_root, "catalogs"),
            DataRoot = Path.Combine(_root, "data"),
        });
        Directory.CreateDirectory(lakehouseOptions.Value.MetadataRoot);
        Directory.CreateDirectory(lakehouseOptions.Value.DataRoot);

        var builder = new DbContextOptionsBuilder<ControlPlaneContext>();
        builder.UseDuckDB($"Data Source={Path.Combine(_root, "control.duckdb")}");
        _context = new ControlPlaneContext(builder.Options);
        await _context.Database.EnsureCreatedAsync();

        _pool = new DucklingPool(lakehouseOptions, NullLoggerFactory.Instance);
        var lakehouse = new LakehouseService(_context, _pool, lakehouseOptions);
        var uploadOptions = Options.Create(new CsvUploadOptions
        {
            MaxBytes = 1024 * 1024,
            MaxAggregateScratchBytes = 1024 * 1024,
            MinimumFreeBytes = 0,
            ScratchRoot = Path.Combine(_root, "scratch"),
        });
        _scratch = new CsvScratchSpace(uploadOptions, TimeProvider.System);
        _uploads = new CsvUploadService(lakehouse, uploadOptions, _scratch);

        await AdminEndpoints.CreateTenantAsync(
            new CreateTenantRequest("acme", "Acme"), _context, TimeProvider.System, default);
        await AdminEndpoints.CreateCatalogAsync(
            "acme", new CreateCatalogRequest("analytics"), _context, lakehouseOptions, TimeProvider.System, default);

        var catalog = await _context.Catalogs.SingleAsync();
        catalog.MetadataKind = CatalogMetadataKind.LocalFile;
        catalog.MetadataSource = Path.Combine(lakehouseOptions.Value.MetadataRoot, "analytics.ducklake");
        catalog.MetadataSchema = null;
        catalog.MetadataSecretName = null;
        await _context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        _scratch.Dispose();
        await _pool.DisposeAsync();
        await _context.DisposeAsync();
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
    public async Task Custom_form_reproduces_the_semicolon_import_and_returns_rejects()
    {
        var http = Request(
            "id;name\r\n1;\"First\"\r\n2\r\n3;\"Third\"\r\n",
            new Dictionary<string, StringValues>
            {
                ["fileName"] = "schedules.csv",
                ["schema"] = "main",
                ["table"] = "predicted_schedules",
                ["mode"] = "custom",
                ["delimiter"] = ";",
                ["quote"] = "\"",
                ["escape"] = "",
                ["newLine"] = "crlf",
                ["header"] = "true",
                ["sampleSize"] = "-1",
                ["ignoreErrors"] = "true",
                ["storeRejects"] = "true",
            });

        var response = await CsvImportEndpoints.ImportAsync(
            http, "acme", "analytics", _uploads, default);

        var imported = Assert.IsType<Ok<CsvImportDto>>(response).Value!;
        Assert.Equal("predicted_schedules", imported.Table);
        Assert.Equal(2, imported.RowsImported);
        Assert.Equal(1, imported.RejectedRows);
        Assert.NotEmpty(imported.Rejects);

        var audit = await _context.QueryRuns.SingleAsync();
        Assert.True(audit.Succeeded);
        Assert.Contains("Browser CSV import", audit.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("lakehold-csv-imports", audit.Sql, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(_scratch.ScratchRoot));
    }

    [Fact]
    public async Task Automatic_form_requires_only_file_and_target()
    {
        var http = Request(
            "id,name\n1,Alice\n",
            new Dictionary<string, StringValues>
            {
                ["fileName"] = "customers.csv",
                ["schema"] = "main",
                ["table"] = "customers",
                ["mode"] = "automatic",
            });

        var response = await CsvImportEndpoints.ImportAsync(
            http, "acme", "analytics", _uploads, default);

        var imported = Assert.IsType<Ok<CsvImportDto>>(response).Value!;
        Assert.Equal(1, imported.RowsImported);
        Assert.Equal(["id", "name"], imported.Columns.Select(column => column.Name));
    }

    [Fact]
    public async Task Automatic_parser_failure_is_sanitized_and_offers_an_explicit_tolerant_retry()
    {
        var csv = new System.Text.StringBuilder("id;name\r\n");
        for (var index = 0; index < 25_000; index++)
        {
            csv.Append(index).Append(";Customer ").Append(index).Append("\r\n");
        }

        // Put the malformed record beyond DuckDB's default 20,480-row sniffing sample. The parser
        // then commits to the two-column dialect before it reaches this one-column row.
        csv.Append("25000\r\n");
        var http = Request(
            csv.ToString(),
            new Dictionary<string, StringValues>
            {
                ["fileName"] = "customers.csv",
                ["schema"] = "main",
                ["table"] = "malformed_customers",
                ["mode"] = "automatic",
            });

        var response = await CsvImportEndpoints.ImportAsync(
            http, "acme", "analytics", _uploads, default);

        var problem = Assert.IsType<ProblemHttpResult>(response);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal("CSV parsing failed", problem.ProblemDetails.Title);
        Assert.Contains("column", problem.ProblemDetails.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Customer", problem.ProblemDetails.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "lakehold-csv-imports",
            problem.ProblemDetails.Detail,
            StringComparison.Ordinal);
        Assert.Equal(
            CsvImportException.ParserErrorCode,
            problem.ProblemDetails.Extensions["code"]);
        Assert.Equal(
            true,
            problem.ProblemDetails.Extensions["canRetryWithTolerantProfile"]);

        var audit = await _context.QueryRuns.SingleAsync();
        Assert.False(audit.Succeeded);
        Assert.Equal(problem.ProblemDetails.Detail, audit.Error);
        Assert.DoesNotContain("Customer", audit.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/", audit.Error, StringComparison.Ordinal);

        var retry = Request(
            csv.ToString(),
            new Dictionary<string, StringValues>
            {
                ["fileName"] = "customers.csv",
                ["schema"] = "main",
                ["table"] = "malformed_customers",
                ["mode"] = "custom",
                ["delimiter"] = ";",
                ["quote"] = "\"",
                ["escape"] = "",
                ["newLine"] = "crlf",
                ["header"] = "true",
                ["sampleSize"] = "-1",
                ["ignoreErrors"] = "true",
                ["storeRejects"] = "true",
            });

        var retryResponse = await CsvImportEndpoints.ImportAsync(
            retry, "acme", "analytics", _uploads, default);

        var imported = Assert.IsType<Ok<CsvImportDto>>(retryResponse).Value!;
        Assert.Equal(25_000, imported.RowsImported);
        Assert.Equal(1, imported.RejectedRows);
        Assert.Equal(2, await _context.QueryRuns.CountAsync());
    }

    [Fact]
    public async Task Upload_service_refuses_oversized_files_before_creating_a_table()
    {
        var tinyOptions = Options.Create(new CsvUploadOptions
        {
            MaxBytes = 4,
            MaxAggregateScratchBytes = 4,
            MinimumFreeBytes = 0,
            ScratchRoot = Path.Combine(_root, "tiny-scratch"),
        });
        using var tinyScratch = new CsvScratchSpace(tinyOptions, TimeProvider.System);
        var tinyUploads = new CsvUploadService(
            new LakehouseService(
                _context,
                _pool,
                Options.Create(new LakehouseOptions
                {
                    MetadataRoot = Path.Combine(_root, "catalogs"),
                    DataRoot = Path.Combine(_root, "data"),
                })),
            tinyOptions,
            tinyScratch);
        var http = Request(
            "id\n123\n",
            new Dictionary<string, StringValues>
            {
                ["fileName"] = "too-large.csv",
                ["schema"] = "main",
                ["table"] = "too_large",
                ["mode"] = "automatic",
            });

        var response = await CsvImportEndpoints.ImportAsync(
            http, "acme", "analytics", tinyUploads, default);

        var problem = Assert.IsType<ProblemHttpResult>(response);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, problem.StatusCode);
        Assert.Empty(_context.QueryRuns);
    }

    [Fact]
    public void Default_ceiling_accepts_the_observed_1_7_gib_production_export()
        => Assert.True(CsvUploadOptions.DefaultMaxBytes > 1_700_000_000L);

    [Fact]
    public async Task Invalid_identifiers_are_refused_without_forging_query_history()
    {
        var http = Request(
            "id\n1\n",
            new Dictionary<string, StringValues>
            {
                ["fileName"] = "customers.csv",
                ["schema"] = "main",
                ["table"] = "safe\n-- forged history",
                ["mode"] = "automatic",
            });

        var response = await CsvImportEndpoints.ImportAsync(
            http, "acme", "analytics", _uploads, default);

        Assert.IsType<BadRequest<string>>(response);
        Assert.Empty(_context.QueryRuns);
    }

    [Fact]
    public async Task Exhausted_node_scratch_capacity_returns_507_without_creating_a_table()
    {
        await using var reservation = await _scratch.AcquireAsync(1024 * 1024, default);
        var http = Request(
            "id\n1\n",
            new Dictionary<string, StringValues>
            {
                ["fileName"] = "customers.csv",
                ["schema"] = "main",
                ["table"] = "customers",
                ["mode"] = "automatic",
            });

        var response = await CsvImportEndpoints.ImportAsync(
            http, "acme", "analytics", _uploads, default);

        var problem = Assert.IsType<ProblemHttpResult>(response);
        Assert.Equal(StatusCodes.Status507InsufficientStorage, problem.StatusCode);
        Assert.Empty(_context.QueryRuns);
    }

    [Fact]
    public async Task Reject_reporting_with_fail_fast_parsing_is_refused_before_reading_the_body()
    {
        var http = Request(
            "id;name\r\n1;Alice\r\n",
            new Dictionary<string, StringValues>
            {
                ["fileName"] = "customers.csv",
                ["schema"] = "main",
                ["table"] = "customers",
                ["mode"] = "custom",
                ["delimiter"] = ";",
                ["quote"] = "\"",
                ["escape"] = "",
                ["newLine"] = "crlf",
                ["header"] = "true",
                ["sampleSize"] = "-1",
                ["ignoreErrors"] = "false",
                ["storeRejects"] = "true",
            });
        var body = new TrackingStream(http.Request.Body);
        http.Request.Body = body;

        var response = await CsvImportEndpoints.ImportAsync(
            http, "acme", "analytics", _uploads, default);

        Assert.IsType<BadRequest<string>>(response);
        Assert.Equal(0, body.ReadCount);
        Assert.Empty(_context.QueryRuns);
    }

    private static DefaultHttpContext Request(
        string contents,
        Dictionary<string, StringValues> fields)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(contents);
        var http = new DefaultHttpContext();
        http.Request.ContentType = "text/csv";
        http.Request.ContentLength = bytes.Length;
        http.Request.Body = new MemoryStream(bytes);
        http.Request.QueryString = QueryString.Create(
            fields.SelectMany(field => field.Value.Select(value =>
                new KeyValuePair<string, string?>(field.Key, value))));
        return http;
    }

    private sealed class TrackingStream(Stream inner) : Stream
    {
        public int ReadCount { get; private set; }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            return inner.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count)
            => inner.Write(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return inner.ReadAsync(buffer, cancellationToken);
        }
    }
}

/// <summary>Covers node-wide scratch reservations and crash-orphan scavenging.</summary>
public sealed class CsvScratchSpaceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lakehold-csv-scratch-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Aggregate_reservations_refuse_capacity_until_the_active_lease_finishes()
    {
        using var scratch = Create(maxAggregateBytes: 8);
        await using var first = await scratch.AcquireAsync(6, default);

        var error = await Assert.ThrowsAsync<CsvScratchCapacityException>(
            () => scratch.AcquireAsync(3, default));
        Assert.Contains("currently reserved", error.Message, StringComparison.Ordinal);

        await first.DisposeAsync();
        await using var retry = await scratch.AcquireAsync(3, default);
        Assert.Equal(3, retry.ReservedBytes);
    }

    [Fact]
    public void Startup_removes_stale_files_but_preserves_recent_ones()
    {
        Directory.CreateDirectory(_root);
        var stale = Path.Combine(_root, "stale.csv");
        var recent = Path.Combine(_root, "recent.csv");
        File.WriteAllText(stale, "stale");
        File.WriteAllText(recent, "recent");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        using var scratch = Create(maxAggregateBytes: 8);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(recent));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failing must not fail the test run.
        }
    }

    private CsvScratchSpace Create(long maxAggregateBytes)
        => new(
            Options.Create(new CsvUploadOptions
            {
                MaxBytes = 8,
                MaxAggregateScratchBytes = maxAggregateBytes,
                MaxConcurrentUploads = 2,
                MinimumFreeBytes = 0,
                ScratchRoot = _root,
                StaleFileAge = TimeSpan.FromDays(1),
            }),
            TimeProvider.System);
}
