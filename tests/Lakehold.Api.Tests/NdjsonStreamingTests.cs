using System.Text;
using System.Text.Json;
using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api.PublicApi;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>Contract tests for bounded-memory query and CDC NDJSON results.</summary>
public sealed class NdjsonStreamingTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-streams", Guid.NewGuid().ToString("N"));
    private ControlPlaneContext _context = null!;
    private DucklingPool _pool = null!;
    private LakehouseService _lakehouse = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var options = Options.Create(new LakehouseOptions
        {
            MetadataRoot = Path.Combine(_root, "catalogs"),
            DataRoot = Path.Combine(_root, "data"),
        });
        var database = new DbContextOptionsBuilder<ControlPlaneContext>()
            .UseDuckDB($"Data Source={Path.Combine(_root, "control.duckdb")}")
            .Options;
        _context = new ControlPlaneContext(database);
        await _context.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Slug = "acme", DisplayName = "Acme", CreatedUtc = DateTimeOffset.UtcNow };
        _context.Tenants.Add(tenant);
        await _context.SaveChangesAsync();
        Directory.CreateDirectory(options.Value.MetadataRoot);
        Directory.CreateDirectory(Path.Combine(options.Value.DataRoot, "analytics"));
        _context.Catalogs.Add(new LakeCatalog
        {
            TenantId = tenant.Id,
            Name = "analytics",
            MetadataKind = CatalogMetadataKind.LocalFile,
            MetadataSource = Path.Combine(options.Value.MetadataRoot, "analytics.ducklake"),
            DataPath = Path.Combine(options.Value.DataRoot, "analytics"),
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        _pool = new DucklingPool(options, NullLoggerFactory.Instance);
        _lakehouse = new LakehouseService(_context, _pool, options);
        await Sql("CREATE TABLE events (id BIGINT, name VARCHAR)");
        await Sql("INSERT INTO events VALUES (1, 'one'), (2, 'two'), (3, 'three')");
    }

    public async Task DisposeAsync()
    {
        await _pool.DisposeAsync();
        await _context.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temporary cleanup must not hide a contract result.
        }
    }

    [Fact]
    public async Task Query_stream_emits_schema_rows_and_terminal_count()
    {
        var context = Context(new MemoryStream());

        await new QueryNdjsonResult(
                _lakehouse,
                "acme",
                "analytics",
                "SELECT id, name FROM events ORDER BY id",
                tokenId: null,
                NullLogger.Instance)
            .ExecuteAsync(context);

        var lines = Lines((MemoryStream)context.Response.Body);
        Assert.Equal("schema", lines[0].GetProperty("type").GetString());
        Assert.Equal(2, lines[0].GetProperty("columns").GetArrayLength());
        Assert.Equal(3, lines.Count(line => line.GetProperty("type").GetString() == "row"));
        Assert.Equal("complete", lines[^1].GetProperty("type").GetString());
        Assert.Equal(3, lines[^1].GetProperty("rowCount").GetInt64());
        Assert.Equal(Ndjson.ContentType, context.Response.ContentType);
    }

    [Fact]
    public async Task Cdc_stream_drains_source_keyset_pages_to_a_frozen_snapshot()
    {
        var latest = (await _lakehouse.GetLatestSnapshotAsync("acme", "analytics", default))!.Value;
        var first = await _lakehouse.GetChangesAsync(
            "acme", "analytics", "main", "events", 0, latest, 1, cursor: null, default);
        Assert.NotNull(first.NextCursor);
        var context = Context(new MemoryStream());

        await new ChangeNdjsonResult(_lakehouse, "acme", "analytics", first, pageSize: 1, NullLogger.Instance)
            .ExecuteAsync(context);

        var lines = Lines((MemoryStream)context.Response.Body);
        Assert.Equal("stream", lines[0].GetProperty("type").GetString());
        Assert.Equal(latest, lines[0].GetProperty("toSnapshot").GetInt64());
        Assert.Equal(3, lines.Count(line => line.GetProperty("type").GetString() == "change"));
        Assert.Equal("complete", lines[^1].GetProperty("type").GetString());
        Assert.Equal(3, lines[^1].GetProperty("changeCount").GetInt64());
    }

    [Fact]
    public async Task Failure_before_the_first_record_is_a_normal_problem_response()
    {
        var context = Context(new MemoryStream());

        await new QueryNdjsonResult(
                _lakehouse,
                "acme",
                "analytics",
                "SELECT * FROM table_that_does_not_exist",
                tokenId: null,
                NullLogger.Instance)
            .ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        var problem = JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray()).RootElement;
        Assert.Equal("query_failed", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Disconnect_after_schema_cancels_query_without_a_false_completion_record()
    {
        using var cancellation = new CancellationTokenSource();
        var body = new CancelAfterFirstLineStream(cancellation);
        var context = Context(body);
        context.RequestAborted = cancellation.Token;

        await new QueryNdjsonResult(
                _lakehouse,
                "acme",
                "analytics",
                "SELECT i FROM range(1000000) values(i)",
                tokenId: null,
                NullLogger.Instance)
            .ExecuteAsync(context);

        var payload = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("\"type\":\"schema\"", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\":\"complete\"", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\":\"error\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unexpected_failure_is_logged_with_the_request_identifier()
    {
        var context = Context(new MemoryStream());
        context.TraceIdentifier = "stream-request-42";
        var logger = new RecordingLogger();
        var failure = new InvalidOperationException("sensitive internal failure");

        await Ndjson.TryWriteErrorAsync(context, failure, logger);

        Assert.Same(failure, logger.Exception);
        Assert.Equal(LogLevel.Error, logger.Level);
        Assert.Contains(context.TraceIdentifier, logger.Message, StringComparison.Ordinal);
        var response = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.DoesNotContain("sensitive internal failure", response, StringComparison.Ordinal);
    }

    private Task<QueryResult> Sql(string sql) => _lakehouse.ExecuteAsync("acme", "analytics", sql, default);

    private static DefaultHttpContext Context(Stream body)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .ConfigureHttpJsonOptions(_ => { })
            .BuildServiceProvider();
        context.Response.Body = body;
        return context;
    }

    private static List<JsonElement> Lines(MemoryStream stream)
        => Encoding.UTF8.GetString(stream.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();

    private sealed class CancelAfterFirstLineStream(CancellationTokenSource cancellation) : MemoryStream
    {
        private bool _cancelled;

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, CancellationToken.None);
            if (!_cancelled && buffer.Span.Contains((byte)'\n'))
            {
                _cancelled = true;
                cancellation.Cancel();
            }
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public LogLevel Level { get; private set; }
        public Exception? Exception { get; private set; }
        public string Message { get; private set; } = string.Empty;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Level = logLevel;
            Exception = exception;
            Message = formatter(state, exception);
        }
    }
}
