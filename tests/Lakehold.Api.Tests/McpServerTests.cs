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
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     The MCP endpoint driven by the SDK's own client over a real HTTP transport, rather than by
///     hand-rolled JSON-RPC — which would prove only that the fixture agrees with itself.
/// </summary>
/// <remarks>
///     Two things here cannot be established any other way, and both were genuine risks in the
///     design: that a tool can reach the request's <c>HttpContext</c> (and therefore the resolved
///     principal) from inside the SDK's dispatch, and that a tool's scoped dependencies resolve
///     against the request's own service scope.
/// </remarks>
public sealed class McpServerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-mcp", Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _demoToken = null!;
    private string _otherToken = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Lakehold:Mcp:Enabled"] = "true",
            ["Lakehold:Mcp:MaxRowsPerResult"] = "5",
        });

        builder.Services.AddDbContext<ControlPlaneContext>(
            o => o.UseDuckDB($"Data Source={Path.Combine(_root, "cp.duckdb")}"));
        builder.Services.AddScoped<ApiTokenAuthenticator>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.Configure<LakeholdAuthOptions>(o => o.RequireAuthentication = false);
        builder.Services.Configure<LakeholdOidcOptions>(_ => { });
        builder.Services.Configure<LakehouseOptions>(o =>
        {
            o.MetadataRoot = Path.Combine(_root, "catalogs");
            o.DataRoot = Path.Combine(_root, "data");
        });
        builder.Services.AddSingleton<DucklingPool>();
        builder.Services.AddSingleton<CatalogCache>();
        builder.Services.AddScoped<LakehouseService>();

        builder.AddLakeholdMcp();

        _app = builder.Build();
        _app.MapLakeholdMcp();
        await _app.StartAsync();

        using var scope = _app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
        await context.Database.EnsureCreatedAsync();

        var demo = new Tenant { Slug = "demo", DisplayName = "Demo", CreatedUtc = DateTimeOffset.UtcNow };
        var other = new Tenant { Slug = "other", DisplayName = "Other", CreatedUtc = DateTimeOffset.UtcNow };
        context.Tenants.AddRange(demo, other);
        await context.SaveChangesAsync();

        // A real catalog, so the query tool can be exercised against the engine rather than only
        // against the authorization path.
        Directory.CreateDirectory(Path.Combine(_root, "catalogs"));
        Directory.CreateDirectory(Path.Combine(_root, "data", "analytics"));
        context.Catalogs.Add(new LakeCatalog
        {
            TenantId = demo.Id,
            Name = "analytics",
            MetadataKind = CatalogMetadataKind.LocalFile,
            MetadataSource = Path.Combine(_root, "catalogs", "analytics.ducklake"),
            DataPath = Path.Combine(_root, "data", "analytics"),
            IsReadOnly = false,
            CreatedUtc = DateTimeOffset.UtcNow,
        });

        var now = DateTimeOffset.UtcNow;
        _demoToken = Persist(context, ApiTokenFactory.Issue(TokenScope.Tenant, demo, "agent", now, role: TokenRole.Owner));
        _otherToken = Persist(context, ApiTokenFactory.Issue(TokenScope.Tenant, other, "agent", now, role: TokenRole.Owner));
        await context.SaveChangesAsync();

        // Initialise the catalog with one read-write statement. A read-only attachment cannot create
        // the DuckLake metadata file, so a catalog that has never been written to cannot be read at
        // all — and the MCP surface attaches read-only always. See "First contact" in docs/MCP.md.
        var lakehouse = scope.ServiceProvider.GetRequiredService<LakehouseService>();
        await lakehouse.ExecuteAsync(
            "demo", "analytics", "CREATE TABLE seeded (i INTEGER)", CancellationToken.None, readOnly: false);
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
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
    public async Task An_authenticated_client_can_list_the_tools()
    {
        await using var client = await ConnectAsync(_demoToken);

        var tools = await client.ListToolsAsync();

        var query = Assert.Single(tools, t => t.Name == "query");
        Assert.False(string.IsNullOrWhiteSpace(query.Description));
    }

    [Fact]
    public async Task Only_the_specified_tools_are_exposed()
    {
        // Maintenance, eject, provisioning, and token minting are deliberately absent (docs/MCP.md).
        // Asserting the whole set means adding a tool is a decision, not an accident.
        await using var client = await ConnectAsync(_demoToken);

        var names = (await client.ListToolsAsync()).Select(t => t.Name).OrderBy(n => n).ToArray();

        Assert.Equal(["query"], names);
    }

    [Fact]
    public async Task A_client_with_no_credential_cannot_connect()
    {
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var client = await ConnectAsync(bearer: null);
            await client.ListToolsAsync();
        });
    }

    [Fact]
    public async Task A_tool_call_reaches_the_principal_and_refuses_another_tenant()
    {
        // Proves the whole chain in one call: the filter resolved a principal, the SDK dispatched to
        // the tool, the tool reached HttpContext and its scoped dependencies, and CapabilityPolicy
        // refused. The refusal must read as "not found" — a forbidden would confirm the tenant exists
        // (invariant 19).
        await using var client = await ConnectAsync(_demoToken);

        var result = await client.CallToolAsync(
            "query",
            new Dictionary<string, object?>
            {
                ["tenant"] = "other",
                ["catalog"] = "otherlake",
                ["sql"] = "SELECT 1",
            });

        Assert.True(result.IsError);
        var text = Text(result);
        Assert.Contains("not found", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("forbidden", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_forbidden_tenant_is_indistinguishable_from_a_missing_catalog()
    {
        // Identical arguments, two credentials. For one the policy refuses before the engine is
        // touched; for the other authorization passes and the catalog genuinely does not exist.
        //
        // The caller must not be able to tell those apart. If the refusals differed — by wording, by
        // error code, by anything — the difference would itself answer "does tenant 'other' exist?",
        // which is the disclosure invariant 19 exists to prevent. So the assertion is equality, and
        // the matching wording in LakeholdTools.Authorize is deliberate rather than coincidental.
        await using var demo = await ConnectAsync(_demoToken);
        await using var other = await ConnectAsync(_otherToken);

        var arguments = new Dictionary<string, object?>
        {
            ["tenant"] = "other",
            ["catalog"] = "c",
            ["sql"] = "SELECT 1",
        };

        var refused = await demo.CallToolAsync("query", arguments);
        var reached = await other.CallToolAsync("query", arguments);

        Assert.True(refused.IsError);
        Assert.True(reached.IsError);
        Assert.Equal(Text(reached), Text(refused));
        Assert.Contains("was not found for tenant 'other'", Text(refused), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_statement_is_refused_before_the_engine()
    {
        await using var client = await ConnectAsync(_demoToken);

        var result = await client.CallToolAsync(
            "query",
            new Dictionary<string, object?> { ["tenant"] = "demo", ["catalog"] = "c", ["sql"] = "   " });

        Assert.True(result.IsError);
        Assert.Contains("SQL statement is required", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_row_cap_is_the_MCP_one_and_says_when_it_bit()
    {
        // MaxRowsPerResult is 5 in this fixture, far below the engine's own ceiling. Invariant 6
        // requires a cap on a materialising path; this asserts the *MCP* number is the one applied,
        // and that truncation is reported rather than left for the agent to infer.
        await using var client = await ConnectAsync(_demoToken);

        var result = await client.CallToolAsync(
            "query",
            new Dictionary<string, object?>
            {
                ["tenant"] = "demo",
                ["catalog"] = "analytics",
                ["sql"] = "SELECT * FROM range(20)",
            });

        Assert.True(result.IsError is not true, Text(result));

        var payload = Payload(result);
        Assert.Equal(5, payload.GetProperty("rowCount").GetInt32());
        Assert.True(payload.GetProperty("truncated").GetBoolean());
        Assert.Equal(5, payload.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task A_write_fails_in_the_engine_even_for_an_owner_credential()
    {
        // The claim the whole surface rests on. This credential is an owner and may write over HTTP;
        // through MCP the catalog is attached read-only regardless, so DuckDB refuses. The refusal
        // comes from the engine, not from a policy check applied to model-generated SQL
        // (invariants 4 and 20).
        await using var client = await ConnectAsync(_demoToken);

        var result = await client.CallToolAsync(
            "query",
            new Dictionary<string, object?>
            {
                ["tenant"] = "demo",
                ["catalog"] = "analytics",
                ["sql"] = "CREATE TABLE mcp_must_not_create (i INTEGER)",
            });

        Assert.True(result.IsError);

        var text = Text(result);

        // Assert on where the refusal came from, not merely that "read-only" appears somewhere. An
        // attach failure also mentions read-only mode, and an earlier version of this test passed on
        // that instead — proving nothing about writes.
        Assert.DoesNotContain("Failed to attach", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read-only", text, StringComparison.OrdinalIgnoreCase);

        // And prove it truly did not happen, rather than trusting the message.
        await using var reader = await ConnectAsync(_demoToken);
        var probe = await reader.CallToolAsync(
            "query",
            new Dictionary<string, object?>
            {
                ["tenant"] = "demo",
                ["catalog"] = "analytics",
                ["sql"] = "SELECT count(*) FROM duckdb_tables() WHERE table_name = 'mcp_must_not_create'",
            });

        Assert.True(probe.IsError is not true, Text(probe));
        Assert.Equal(0, Payload(probe).GetProperty("rows")[0][0].GetInt32());
    }

    [Fact]
    public async Task Columns_carry_their_type_so_an_agent_can_write_the_next_query()
    {
        await using var client = await ConnectAsync(_demoToken);

        var result = await client.CallToolAsync(
            "query",
            new Dictionary<string, object?>
            {
                ["tenant"] = "demo",
                ["catalog"] = "analytics",
                ["sql"] = "SELECT 1 AS n, 'x' AS s",
            });

        Assert.True(result.IsError is not true, Text(result));

        var columns = Payload(result).GetProperty("columns");
        Assert.Equal(2, columns.GetArrayLength());
        Assert.Equal("n", columns[0].GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(columns[0].GetProperty("type").GetString()));
    }

    private static System.Text.Json.JsonElement Payload(ModelContextProtocol.Protocol.CallToolResult result) =>
        result.StructuredContent
        ?? System.Text.Json.JsonDocument.Parse(Text(result)).RootElement;

    private static string Text(ModelContextProtocol.Protocol.CallToolResult result) =>
        string.Join(" ", result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));

    private static string Persist(ControlPlaneContext context, IssuedToken issued)
    {
        context.ApiTokens.Add(issued.Record);
        return issued.Plaintext;
    }

    private async Task<McpClient> ConnectAsync(string? bearer)
    {
        var httpClient = _app.GetTestClient();
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        };

        if (bearer is not null)
        {
            options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + bearer };
        }

        var transport = new HttpClientTransport(options, httpClient);
        return await McpClient.CreateAsync(transport);
    }
}
