using System.Text.Json;
using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api.Auth;
using Lakehold.Api.Mcp;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     The write tool, and the two gates in front of it: the operator's switch and the credential.
/// </summary>
/// <remarks>
///     The interesting assertion is not that writes work — it is that <c>query</c> stays read-only
///     when they are enabled, and that the tool <em>list</em> reflects the mode. A client decides
///     whether to prompt a human from the annotations it reads, so a deployment that permits writes
///     has to say so in the surface rather than only in its configuration.
/// </remarks>
public sealed class McpWriteToolTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-mcp-write", Guid.NewGuid().ToString("N"));
    private readonly List<WebApplication> _apps = [];
    private string _writerToken = null!;
    private string _readerToken = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var app in _apps)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failing must not fail the run.
        }
    }

    [Fact]
    public async Task The_write_tool_is_absent_unless_the_operator_enables_it()
    {
        // The default. An operator who upgrades does not silently acquire an agent that can mutate the
        // lakehouse, and a client can see that from the tool list alone.
        var app = await StartAsync(allowWrites: false);
        await using var client = await ConnectAsync(app, _writerToken);

        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToArray();

        Assert.DoesNotContain("execute", names);
    }

    [Fact]
    public async Task Enabling_writes_advertises_a_destructive_tool_rather_than_loosening_query()
    {
        var app = await StartAsync(allowWrites: true);
        await using var client = await ConnectAsync(app, _writerToken);

        var tools = await client.ListToolsAsync();

        // The annotations are what a client uses to decide whether to ask a human, so they have to be
        // true of the tool as registered — which is why writes are a separate tool and not a mode.
        var execute = Assert.Single(tools, t => t.Name == "execute");
        Assert.False(execute.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(execute.ProtocolTool.Annotations?.DestructiveHint);

        var query = Assert.Single(tools, t => t.Name == "query");
        Assert.True(query.ProtocolTool.Annotations?.ReadOnlyHint);
    }

    [Fact]
    public async Task No_mutating_tool_is_advertised_unless_the_operator_enables_writes()
    {
        // The gate is the tool's own read-only annotation, not a list of names. Asserting over every
        // advertised tool means adding a mutating tool cannot quietly land outside the gate, which is
        // how the connector control plane first shipped reachable with writes disabled.
        var app = await StartAsync(allowWrites: false);
        await using var client = await ConnectAsync(app, _writerToken);

        var tools = await client.ListToolsAsync();

        Assert.NotEmpty(tools);
        Assert.All(tools, tool => Assert.True(
            tool.ProtocolTool.Annotations?.ReadOnlyHint,
            $"'{tool.Name}' is advertised while writes are disabled but is not annotated read-only."));
    }

    [Theory]
    [InlineData("execute")]
    [InlineData("create_connector")]
    [InlineData("update_connector")]
    [InlineData("retire_connector")]
    [InlineData("run_connector")]
    [InlineData("retry_connector")]
    [InlineData("pause_connector")]
    [InlineData("resume_connector")]
    public async Task A_mutating_tool_is_refused_by_name_while_writes_are_disabled(string tool)
    {
        // Removing a tool from discovery is not enforcement. A client with a cached tool list calls
        // it by name, so the call has to fail too — and fail before the tool does any work.
        var app = await StartAsync(allowWrites: false);
        await using var client = await ConnectAsync(app, _writerToken);

        var arguments = new Dictionary<string, object?>
        {
            ["tenant"] = "demo",
            ["catalog"] = "analytics",
            ["sql"] = "CREATE TABLE writes_are_disabled (id INTEGER)",
            ["id"] = 1,
            ["version"] = 1,
            ["definition"] = null,
        };

        // A refusal reaches the caller either as a protocol error or as an error result, depending on
        // where in the pipeline it is raised. Both are refusals; neither may be a completed write.
        string message;
        try
        {
            var result = await client.CallToolAsync(tool, arguments);
            Assert.True(result.IsError, $"'{tool}' was executed while writes are disabled.");
            message = Text(result);
        }
        catch (Exception exception)
        {
            message = exception.Message;
        }

        Assert.Contains("disabled", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Enabling_writes_advertises_the_connector_control_plane_as_mutating()
    {
        var app = await StartAsync(allowWrites: true);
        await using var client = await ConnectAsync(app, _writerToken);

        var tools = await client.ListToolsAsync();

        // A connector run replaces a full-snapshot target, so the annotation a client uses to decide
        // whether to ask a human has to say so.
        var run = Assert.Single(tools, t => t.Name == "run_connector");
        Assert.False(run.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(run.ProtocolTool.Annotations?.DestructiveHint);

        var list = Assert.Single(tools, t => t.Name == "list_connectors");
        Assert.True(list.ProtocolTool.Annotations?.ReadOnlyHint);
    }

    [Fact]
    public async Task A_read_write_credential_can_write_when_the_operator_allows_it()
    {
        var app = await StartAsync(allowWrites: true);
        await using var client = await ConnectAsync(app, _writerToken);

        var created = await client.CallToolAsync(
            "execute",
            new Dictionary<string, object?>
            {
                ["tenant"] = "demo",
                ["catalog"] = "analytics",
                ["sql"] = "CREATE TABLE written (i INTEGER)",
            });

        Assert.True(created.IsError is not true, Text(created));

        // Prove it landed rather than trusting the response.
        var probe = await client.CallToolAsync(
            "query",
            new Dictionary<string, object?>
            {
                ["tenant"] = "demo",
                ["catalog"] = "analytics",
                ["sql"] = "SELECT count(*) FROM duckdb_tables() WHERE table_name = 'written'",
            });

        Assert.True(probe.IsError is not true, Text(probe));
        Assert.Equal(1, Payload(probe).GetProperty("rows")[0][0].GetInt32());
    }

    [Fact]
    public async Task Query_stays_read_only_even_where_writes_are_enabled()
    {
        // The read tool must never become a write path. Otherwise its read-only annotation is false
        // and a client stops prompting for something that mutates.
        var app = await StartAsync(allowWrites: true);
        await using var client = await ConnectAsync(app, _writerToken);

        var result = await client.CallToolAsync(
            "query",
            new Dictionary<string, object?>
            {
                ["tenant"] = "demo",
                ["catalog"] = "analytics",
                ["sql"] = "CREATE TABLE query_must_not_create (i INTEGER)",
            });

        Assert.True(result.IsError);
        Assert.Contains("read-only", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_read_only_credential_cannot_write_even_where_writes_are_enabled()
    {
        // The second gate. The engine would refuse anyway on a read-only attachment; the tool says so
        // in terms the agent can act on instead of a DuckDB error about the catalog.
        var app = await StartAsync(allowWrites: true);
        await using var client = await ConnectAsync(app, _readerToken);

        var result = await client.CallToolAsync(
            "execute",
            new Dictionary<string, object?>
            {
                ["tenant"] = "demo",
                ["catalog"] = "analytics",
                ["sql"] = "CREATE TABLE reader_must_not_create (i INTEGER)",
            });

        Assert.True(result.IsError);
        Assert.Contains("read-only", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_write_to_another_tenant_is_refused_without_disclosing_it()
    {
        var app = await StartAsync(allowWrites: true);
        await using var client = await ConnectAsync(app, _writerToken);

        var result = await client.CallToolAsync(
            "execute",
            new Dictionary<string, object?>
            {
                ["tenant"] = "other",
                ["catalog"] = "c",
                ["sql"] = "CREATE TABLE t (i INTEGER)",
            });

        Assert.True(result.IsError);
        Assert.Contains("was not found for tenant 'other'", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_saved_write_setting_applies_to_an_existing_server_and_client()
    {
        var app = await StartAsync(allowWrites: false);
        await using var client = await ConnectAsync(app, _writerToken);
        Assert.DoesNotContain("execute", (await client.ListToolsAsync()).Select(tool => tool.Name));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<McpRuntimeSettingsStore>()
                .SaveAsync(
                    enabled: true,
                    allowWrites: true,
                    maxRowsPerResult: 200,
                    publicBaseUrl: null,
                    expectedVersion: 0,
                    CancellationToken.None);
        }

        Assert.Contains("execute", (await client.ListToolsAsync()).Select(tool => tool.Name));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<McpRuntimeSettingsStore>()
                .SaveAsync(
                    enabled: true,
                    allowWrites: false,
                    maxRowsPerResult: 200,
                    publicBaseUrl: null,
                    expectedVersion: 1,
                    CancellationToken.None);
        }

        // The client discovered execute while it was enabled. A direct call from that stale cache
        // must still observe the just-saved setting instead of reaching DuckDB.
        var blocked = await client.CallToolAsync(
            "execute",
            new Dictionary<string, object?>
            {
                ["tenant"] = "demo",
                ["catalog"] = "analytics",
                ["sql"] = "CREATE TABLE must_not_exist (i INTEGER)",
            });

        Assert.True(blocked.IsError);
        Assert.Contains("disabled", Text(blocked), StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement Payload(CallToolResult result) =>
        result.StructuredContent ?? JsonDocument.Parse(Text(result)).RootElement;

    private static string Text(CallToolResult result) =>
        string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private async Task<WebApplication> StartAsync(bool allowWrites)
    {
        // Each case gets its own state root so catalogs and persisted runtime settings stay independent.
        var root = Path.Combine(_root, allowWrites ? "rw" : "ro", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Lakehold:Mcp:Enabled"] = "true",
            ["Lakehold:Mcp:AllowWrites"] = allowWrites ? "true" : "false",
        });

        builder.Services.AddDbContext<ControlPlaneContext>(
            o => o.UseDuckDB($"Data Source={Path.Combine(root, "cp.duckdb")}"));
        builder.Services.AddScoped<ApiTokenAuthenticator>();
        builder.Services.AddScoped<MemberDirectory>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.Configure<LakeholdOidcOptions>(_ => { });
        builder.Services.Configure<LakehouseOptions>(o =>
        {
            o.MetadataRoot = Path.Combine(root, "catalogs");
            o.DataRoot = Path.Combine(root, "data");
        });
        builder.Services.AddSingleton<DucklingPool>();
        builder.Services.AddSingleton<CatalogCache>();
        builder.Services.AddScoped<LakehouseService>();
        builder.AddLakeholdMcp();

        var app = builder.Build();
        app.MapLakeholdMcp();
        await app.StartAsync();
        _apps.Add(app);

        using var scope = app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
        await context.Database.EnsureCreatedAsync();

        var demo = new Tenant { Slug = "demo", DisplayName = "Demo", CreatedUtc = DateTimeOffset.UtcNow };
        var other = new Tenant { Slug = "other", DisplayName = "Other", CreatedUtc = DateTimeOffset.UtcNow };
        context.Tenants.AddRange(demo, other);
        await context.SaveChangesAsync();

        Directory.CreateDirectory(Path.Combine(root, "catalogs"));
        Directory.CreateDirectory(Path.Combine(root, "data", "analytics"));
        context.Catalogs.Add(new LakeCatalog
        {
            TenantId = demo.Id,
            Name = "analytics",
            MetadataKind = CatalogMetadataKind.LocalFile,
            MetadataSource = Path.Combine(root, "catalogs", "analytics.ducklake"),
            DataPath = Path.Combine(root, "data", "analytics"),
            IsReadOnly = false,
            CreatedUtc = DateTimeOffset.UtcNow,
        });

        var now = DateTimeOffset.UtcNow;
        _writerToken = Persist(context, ApiTokenFactory.Issue(TokenScope.Tenant, demo, "writer", now, role: TokenRole.Editor));
        _readerToken = Persist(context, ApiTokenFactory.Issue(TokenScope.Tenant, demo, "reader", now, role: TokenRole.Reader));
        await context.SaveChangesAsync();

        // A read-only attachment cannot create the metadata file, so the catalog is initialised once
        // through a read-write statement. See "First contact" in docs/MCP.md.
        var lakehouse = scope.ServiceProvider.GetRequiredService<LakehouseService>();
        await lakehouse.ExecuteAsync(
            "demo", "analytics", "CREATE TABLE seeded (i INTEGER)", CancellationToken.None, readOnly: false);

        return app;
    }

    private static string Persist(ControlPlaneContext context, IssuedToken issued)
    {
        context.ApiTokens.Add(issued.Record);
        return issued.Plaintext;
    }

    private static async Task<McpClient> ConnectAsync(WebApplication app, string bearer)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + bearer },
            },
            app.GetTestClient());

        return await McpClient.CreateAsync(transport);
    }
}
