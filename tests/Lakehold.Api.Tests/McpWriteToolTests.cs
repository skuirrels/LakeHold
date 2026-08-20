using System.Text.Json;
using System.Security.Claims;
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
    private const string MemberBearer = "member-test-token";
    private const string WrongAudienceBearer = "wrong-audience-test-token";
    private const string MemberSubject = "member-operator";

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

    [Fact]
    public async Task Every_mutating_tool_is_refused_by_name_while_writes_are_disabled()
    {
        // The companion to the discovery assertion, and it has to be derived the same way. A hand-kept
        // [InlineData] list covered eight of the fourteen mutating tools while the documentation
        // claimed all of them, which is the same drift the annotation gate was introduced to end.
        // Enumerating the compiled tool set instead means a mutating tool added later is covered the
        // moment it is registered.
        var allowed = await StartAsync(allowWrites: true, allowOperatorCommands: true);
        string[] mutating;
        await using (var discovery = await ConnectAsync(allowed, _writerToken))
        {
            mutating =
            [
                .. (await discovery.ListToolsAsync())
                    .Where(tool => tool.ProtocolTool.Annotations?.ReadOnlyHint is not true)
                    .Select(tool => tool.Name),
            ];
        }

        // Sanity: the enumeration found the surface, rather than finding nothing and asserting nothing.
        Assert.Contains("execute", mutating);
        Assert.True(mutating.Length >= 14, $"Only {mutating.Length} mutating tools were discovered.");

        var app = await StartAsync(allowWrites: false, allowOperatorCommands: true);
        await using var client = await ConnectAsync(app, _writerToken);

        foreach (var tool in mutating)
        {
            await AssertRefusedAsync(client, tool);
        }
    }

    private static async Task AssertRefusedAsync(McpClient client, string tool)
    {
        // Removing a tool from discovery is not enforcement. A client with a cached tool list calls
        // it by name, so the call has to fail too — and fail before the tool does any work.
        var arguments = new Dictionary<string, object?>
        {
            ["tenant"] = "demo",
            ["catalog"] = "analytics",
            ["sql"] = "CREATE TABLE writes_are_disabled (id INTEGER)",
            ["source"] = "SELECT 1",
            ["name"] = "refused",
            ["viewName"] = "refused_view",
            ["table"] = "seeded",
            ["operation"] = "flush",
            ["snapshotId"] = 1L,
            ["currentSnapshotId"] = 1L,
            ["id"] = 1,
            ["revision"] = 1,
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
    public async Task An_agent_exceeding_the_request_ceiling_is_shed_rather_than_served()
    {
        // An agent decides its next call from the result of the last one, so a loop that misreads a
        // refusal issues requests as fast as the network allows and does not get bored. Nothing else
        // on this surface bounds that: the row ceiling bounds one result, not how many are asked for.
        var app = await StartAsync(allowWrites: false, requestsPerMinute: 3);

        var refused = 0;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/mcp");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _writerToken);
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            request.Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
                System.Text.Encoding.UTF8,
                "application/json");

            using var response = await app.GetTestClient().SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                refused++;
                Assert.True(
                    response.Headers.RetryAfter is not null,
                    "A shed request must say when to come back.");
            }
        }

        Assert.True(refused > 0, "The ceiling never bit; the limiter is not in the pipeline.");
    }

    [Fact]
    public async Task Cycling_credentials_does_not_buy_more_budget()
    {
        // The credential is caller-supplied, so partitioning on it alone means a fresh random bearer
        // per request lands in a new partition every time and is never limited — unlimited traffic,
        // one rate-limiter partition per value, and a token lookup for each, which is precisely what
        // running the limiter ahead of authentication is supposed to prevent. A peer ceiling backs it.
        var app = await StartAsync(allowWrites: false, requestsPerMinute: 2);

        var refused = 0;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/mcp");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer lkh_" + Guid.NewGuid().ToString("N"));
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            request.Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
                System.Text.Encoding.UTF8,
                "application/json");

            using var response = await app.GetTestClient().SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                refused++;
            }
        }

        Assert.True(refused > 0, "A caller cycling credentials was never shed.");
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
    public async Task Mcp_records_exactly_one_token_actor_and_its_origin()
    {
        var app = await StartAsync(allowWrites: true);
        await using var client = await ConnectAsync(app, _writerToken);

        var result = await client.CallToolAsync(
            "execute",
            new Dictionary<string, object?>
            {
                ["tenant"] = "demo",
                ["catalog"] = "analytics",
                ["sql"] = "CREATE TABLE token_audit (i INTEGER)",
            });
        Assert.True(result.IsError is not true, Text(result));

        await using var scope = app.Services.CreateAsyncScope();
        var run = await scope.ServiceProvider.GetRequiredService<ControlPlaneContext>().QueryRuns
            .AsNoTracking()
            .SingleAsync(item => item.Sql == "CREATE TABLE token_audit (i INTEGER)");

        Assert.NotNull(run.TokenId);
        Assert.Null(run.MemberId);
        Assert.Equal(QueryActorKind.ApiToken, run.ActorKind);
        Assert.Equal(QueryOrigin.Mcp, run.Origin);
    }

    [Fact]
    public async Task Mcp_records_exactly_one_member_actor_and_its_origin()
    {
        var app = await StartAsync(allowWrites: false);
        await using var client = await ConnectAsync(app, MemberBearer);

        var result = await client.CallToolAsync(
            "query",
            new Dictionary<string, object?>
            {
                ["tenant"] = "demo",
                ["catalog"] = "analytics",
                ["sql"] = "SELECT 42 AS answer",
            });
        Assert.True(result.IsError is not true, Text(result));

        await using var scope = app.Services.CreateAsyncScope();
        var run = await scope.ServiceProvider.GetRequiredService<ControlPlaneContext>().QueryRuns
            .AsNoTracking()
            .SingleAsync(item => item.Sql == "SELECT 42 AS answer");

        Assert.Null(run.TokenId);
        Assert.NotNull(run.MemberId);
        Assert.Equal(QueryActorKind.Member, run.ActorKind);
        Assert.Equal(QueryOrigin.Mcp, run.Origin);
    }

    [Fact]
    public async Task Mcp_refuses_a_member_token_issued_for_another_resource_opaquely()
    {
        var app = await StartAsync(allowWrites: false);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", WrongAudienceBearer);

        using var response = await client.PostAsync(new Uri("/mcp", UriKind.Relative), content: null);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("resource_metadata", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("audience", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
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
                    allowOperatorCommands: false,
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
                    allowOperatorCommands: false,
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

    [Fact]
    public async Task Operator_commands_are_discovered_and_enforced_from_the_same_tool_metadata()
    {
        var app = await StartAsync(allowWrites: true, allowOperatorCommands: false);
        await using var client = await ConnectAsync(app, _writerToken);
        Assert.DoesNotContain("plan_maintenance", (await client.ListToolsAsync()).Select(tool => tool.Name));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<McpRuntimeSettingsStore>()
                .SaveAsync(
                    enabled: true,
                    allowWrites: true,
                    allowOperatorCommands: true,
                    maxRowsPerResult: 200,
                    publicBaseUrl: null,
                    expectedVersion: 0,
                    CancellationToken.None);
        }

        Assert.Contains("plan_maintenance", (await client.ListToolsAsync()).Select(tool => tool.Name));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<McpRuntimeSettingsStore>()
                .SaveAsync(
                    enabled: true,
                    allowWrites: true,
                    allowOperatorCommands: false,
                    maxRowsPerResult: 200,
                    publicBaseUrl: null,
                    expectedVersion: 1,
                    CancellationToken.None);
        }

        var blocked = await client.CallToolAsync(
            "plan_maintenance",
            new Dictionary<string, object?>
            {
                ["tenant"] = "demo",
                ["catalog"] = "analytics",
                ["operation"] = "compact",
            });
        Assert.True(blocked.IsError);
        Assert.Contains("disabled", Text(blocked), StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement Payload(CallToolResult result) =>
        result.StructuredContent ?? JsonDocument.Parse(Text(result)).RootElement;

    private static string Text(CallToolResult result) =>
        string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private async Task<WebApplication> StartAsync(
        bool allowWrites,
        bool allowOperatorCommands = false,
        int? requestsPerMinute = null)
    {
        // Each case gets its own state root so catalogs and persisted runtime settings stay independent.
        var root = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Lakehold:Mcp:Enabled"] = "true",
            ["Lakehold:Mcp:AllowWrites"] = allowWrites ? "true" : "false",
            ["Lakehold:Mcp:AllowOperatorCommands"] = allowOperatorCommands ? "true" : "false",
            ["Lakehold:Mcp:PublicBaseUrl"] = "http://localhost",
            ["Lakehold:Mcp:RequestsPerMinutePerCredential"] =
                requestsPerMinute?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

        builder.Services.Configure<LakeholdOidcOptions>(options =>
        {
            options.Authority = "https://idp.example.com/realms/lakehold";
            options.Audience = "lakehold-api";
        });
        builder.AddLakeholdMcpForTests(root);

        var app = builder.Build();
        app.UseRateLimiter();
        app.Use(async (http, next) =>
        {
            var authorization = http.Request.Headers.Authorization.ToString();
            if (authorization is "Bearer " + MemberBearer or "Bearer " + WrongAudienceBearer)
            {
                http.User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", MemberSubject),
                    new Claim(
                        "aud",
                        authorization.EndsWith(MemberBearer, StringComparison.Ordinal)
                            ? "http://localhost/mcp"
                            : "another-client"),
                ], "test"));
            }

            await next(http).ConfigureAwait(false);
        });
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

        context.TenantMembers.Add(new TenantMember
        {
            TenantId = demo.Id,
            Issuer = "https://idp.example.com/realms/lakehold",
            Subject = MemberSubject,
            DisplayName = "Member Operator",
            Role = TokenRole.Editor,
            Status = MemberStatus.Active,
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
