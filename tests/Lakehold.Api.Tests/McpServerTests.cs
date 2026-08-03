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
    private string _narrowedToken = null!;

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
        _narrowedToken = Persist(context, ApiTokenFactory.Issue(
            TokenScope.Tenant, demo, "narrowed", now, catalogName: "analytics", role: TokenRole.Reader));
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

        Assert.Equal(
            ["describe_schema", "get_snapshot", "list_changes", "list_snapshots", "list_tenants", "query", "query_snapshot"],
            names);
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
    public async Task A_saved_row_cap_applies_to_the_next_call_without_restarting()
    {
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<McpRuntimeSettingsStore>()
                .SaveAsync(
                    enabled: true,
                    allowWrites: false,
                    maxRowsPerResult: 2,
                    publicBaseUrl: null,
                    expectedVersion: 0,
                    CancellationToken.None);
        }

        try
        {
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
            Assert.Equal(2, Payload(result).GetProperty("rowCount").GetInt32());
        }
        finally
        {
            await using var scope = _app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<McpRuntimeSettingsStore>()
                .SaveAsync(
                    enabled: true,
                    allowWrites: false,
                    maxRowsPerResult: 5,
                    publicBaseUrl: null,
                    expectedVersion: 1,
                    CancellationToken.None);
        }
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

    [Fact]
    public async Task List_tenants_is_the_entry_point_and_is_scoped_to_the_credential()
    {
        // Without this an agent has to be told the names in its prompt, and guesses wrongly when it
        // is not. It must show its own tenant and no one else's.
        await using var client = await ConnectAsync(_demoToken);

        var result = await client.CallToolAsync("list_tenants", new Dictionary<string, object?>());

        Assert.True(result.IsError is not true, Text(result));

        var tenants = Payload(result);
        Assert.Equal(1, tenants.GetArrayLength());
        Assert.Equal("demo", tenants[0].GetProperty("tenant").GetString());
        Assert.Equal("analytics", tenants[0].GetProperty("catalogs")[0].GetProperty("catalog").GetString());
    }

    [Fact]
    public async Task List_tenants_does_not_name_another_tenant()
    {
        await using var client = await ConnectAsync(_otherToken);

        var result = await client.CallToolAsync("list_tenants", new Dictionary<string, object?>());

        Assert.True(result.IsError is not true, Text(result));
        Assert.DoesNotContain("demo", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_narrowed_credential_is_shown_only_the_catalog_it_can_reach()
    {
        // Deliberately stricter than the HTTP listing route, which returns every catalog the tenant
        // owns. Naming a catalog this credential cannot query would waste the agent's next call.
        await using var client = await ConnectAsync(_narrowedToken);

        var result = await client.CallToolAsync("list_tenants", new Dictionary<string, object?>());

        Assert.True(result.IsError is not true, Text(result));

        var catalogs = Payload(result)[0].GetProperty("catalogs");
        Assert.Equal(1, catalogs.GetArrayLength());
        Assert.Equal("analytics", catalogs[0].GetProperty("catalog").GetString());
    }

    [Fact]
    public async Task Describe_schema_returns_columns_and_hides_ducklake_internals()
    {
        // The filtering matters more here than in the workbench: a human scrolls past the internal
        // tables, an agent reads them and reasons about them as though they were the tenant's data.
        await using var client = await ConnectAsync(_demoToken);

        var result = await client.CallToolAsync(
            "describe_schema",
            new Dictionary<string, object?> { ["tenant"] = "demo", ["catalog"] = "analytics" });

        Assert.True(result.IsError is not true, Text(result));

        var text = Text(result);
        Assert.Contains("seeded", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ducklake_", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Describe_schema_refuses_another_tenant_without_disclosing_it()
    {
        await using var client = await ConnectAsync(_demoToken);

        var result = await client.CallToolAsync(
            "describe_schema",
            new Dictionary<string, object?> { ["tenant"] = "other", ["catalog"] = "c" });

        Assert.True(result.IsError);
        Assert.Contains("was not found for tenant 'other'", Text(result), StringComparison.Ordinal);
        Assert.DoesNotContain("forbidden", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_schema_resource_is_a_template_that_discloses_nothing_by_being_listed()
    {
        // Templates rather than concrete resources on purpose: enumerating every reachable catalog
        // would mean resolving the credential during resource listing, which is the one place a
        // mistake would hand catalog names to a caller that cannot reach them.
        await using var client = await ConnectAsync(_demoToken);

        var templates = await client.ListResourceTemplatesAsync();
        var concrete = await client.ListResourcesAsync();

        Assert.Contains(templates, r => r.UriTemplate == "lakehold://{tenant}/{catalog}/schema");
        Assert.Empty(concrete);
    }

    [Fact]
    public async Task The_schema_resource_returns_the_schema()
    {
        await using var client = await ConnectAsync(_demoToken);

        var result = await client.ReadResourceAsync("lakehold://demo/analytics/schema");

        var text = string.Join(
            " ",
            result.Contents.OfType<ModelContextProtocol.Protocol.TextResourceContents>().Select(c => c.Text));

        Assert.Contains("seeded", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ducklake_", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_schema_resource_authorises_exactly_as_a_tool_does()
    {
        // The hole this closes: a resource that skipped the capability check would expose every
        // tenant's schema by URI while the tools stayed correct.
        await using var client = await ConnectAsync(_demoToken);

        var failure = await Assert.ThrowsAnyAsync<Exception>(
            async () => await client.ReadResourceAsync("lakehold://other/c/schema"));

        Assert.Contains("was not found for tenant 'other'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_snapshots_returns_the_history_newest_first()
    {
        await using var client = await ConnectAsync(_demoToken);

        var result = await client.CallToolAsync(
            "list_snapshots",
            new Dictionary<string, object?> { ["tenant"] = "demo", ["catalog"] = "analytics" });

        Assert.True(result.IsError is not true, Text(result));

        var snapshots = Payload(result);
        Assert.True(snapshots.GetArrayLength() > 0);

        var ids = snapshots.EnumerateArray().Select(s => s.GetProperty("snapshotId").GetInt64()).ToArray();
        Assert.Equal(ids.OrderByDescending(i => i), ids);
    }

    [Fact]
    public async Task List_snapshots_refuses_another_tenant_without_disclosing_it()
    {
        await using var client = await ConnectAsync(_demoToken);

        var result = await client.CallToolAsync(
            "list_snapshots",
            new Dictionary<string, object?> { ["tenant"] = "other", ["catalog"] = "c" });

        Assert.True(result.IsError);
        Assert.Contains("was not found for tenant 'other'", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_changes_reports_an_insert_and_bounds_the_range_it_read()
    {
        // A real write, then the feed. Omitting toSnapshot must read to the newest snapshot — which is
        // also what keeps an agent clear of verified behaviour 7, where a range ending before the
        // table existed raises.
        await SeedChangeAsync();

        await using var client = await ConnectAsync(_demoToken);

        var result = await client.CallToolAsync(
            "list_changes",
            new Dictionary<string, object?>
            {
                ["tenant"] = "demo",
                ["catalog"] = "analytics",
                ["table"] = "changed",
                ["fromSnapshot"] = 0,
            });

        Assert.True(result.IsError is not true, Text(result));

        var page = Payload(result);
        Assert.Equal("changed", page.GetProperty("table").GetString());
        Assert.True(page.GetProperty("toSnapshot").GetInt64() >= page.GetProperty("fromSnapshot").GetInt64());

        var changes = page.GetProperty("changes");
        Assert.True(changes.GetArrayLength() > 0);
        Assert.Equal("insert", changes[0].GetProperty("change").GetString());
    }

    [Fact]
    public async Task List_changes_is_inclusive_at_both_ends()
    {
        // Invariant 18 and verified behaviour 6. If the lower bound were exclusive, asking from the
        // snapshot that made the change would return it — and a consumer resuming at L + 1 would skip
        // a window rather than replay one. Asking from *above* the change must return nothing.
        await SeedChangeAsync();

        await using var client = await ConnectAsync(_demoToken);

        var all = await client.CallToolAsync(
            "list_changes",
            new Dictionary<string, object?>
            {
                ["tenant"] = "demo",
                ["catalog"] = "analytics",
                ["table"] = "changed",
                ["fromSnapshot"] = 0,
            });

        var last = Payload(all).GetProperty("toSnapshot").GetInt64();

        var after = await client.CallToolAsync(
            "list_changes",
            new Dictionary<string, object?>
            {
                ["tenant"] = "demo",
                ["catalog"] = "analytics",
                ["table"] = "changed",
                ["fromSnapshot"] = last + 1,
            });

        Assert.True(after.IsError is not true, Text(after));
        Assert.Equal(0, Payload(after).GetProperty("changes").GetArrayLength());
    }

    [Fact]
    public async Task List_changes_forwards_the_engines_complaint_about_an_unknown_table()
    {
        await using var client = await ConnectAsync(_demoToken);

        var result = await client.CallToolAsync(
            "list_changes",
            new Dictionary<string, object?>
            {
                ["tenant"] = "demo",
                ["catalog"] = "analytics",
                ["table"] = "no_such_table",
                ["fromSnapshot"] = 0,
            });

        // Forwarded rather than paraphrased: the engine names the problem, and that is what lets an
        // agent correct itself on the next call.
        Assert.True(result.IsError);
        Assert.False(string.IsNullOrWhiteSpace(Text(result)));
    }

    [Fact]
    public async Task Every_list_shaped_tool_honours_the_MCP_page_ceiling()
    {
        // The fixture's ceiling is 5. A change feed page defaults to 1000 over HTTP and admits 10000 —
        // right for a consumer writing to a database, wrong for a context window. The ceiling has to
        // apply to every list-shaped tool, not only to query, or the budget it exists to keep is
        // defeated by the tool that returns the most rows.
        await SeedChangeAsync(rows: 12);

        await using var client = await ConnectAsync(_demoToken);

        var changes = await client.CallToolAsync(
            "list_changes",
            new Dictionary<string, object?>
            {
                ["tenant"] = "demo",
                ["catalog"] = "analytics",
                ["table"] = "changed",
                ["fromSnapshot"] = 0,
                ["limit"] = 10_000,
            });

        Assert.True(changes.IsError is not true, Text(changes));
        Assert.True(Payload(changes).GetProperty("changes").GetArrayLength() <= 5);

        var snapshots = await client.CallToolAsync(
            "list_snapshots",
            new Dictionary<string, object?> { ["tenant"] = "demo", ["catalog"] = "analytics", ["limit"] = 500 });

        Assert.True(snapshots.IsError is not true, Text(snapshots));
        Assert.True(Payload(snapshots).GetArrayLength() <= 5);
    }

    /// <summary>Creates a table and inserts a row, so the change feed has something to report.</summary>
    private async Task SeedChangeAsync(int rows = 1)
    {
        using var scope = _app.Services.CreateScope();
        var lakehouse = scope.ServiceProvider.GetRequiredService<LakehouseService>();

        await lakehouse.ExecuteAsync(
            "demo", "analytics", "CREATE TABLE IF NOT EXISTS changed (i INTEGER)", CancellationToken.None,
            readOnly: false);

        for (var i = 0; i < rows; i++)
        {
            // One statement per row, so each lands in its own snapshot and the history is long enough
            // to exercise a page ceiling.
            await lakehouse.ExecuteAsync(
                "demo", "analytics", $"INSERT INTO changed VALUES ({i})", CancellationToken.None,
                readOnly: false);
        }
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
