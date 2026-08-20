using System.Text.Json;
using Lakehold.Api.Auth;
using Lakehold.Api.Mcp;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Lakehold.Engine.Catalog;
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
///     Every tool driven through the SDK client, against a host that can actually construct it.
/// </summary>
/// <remarks>
///     <para>
///         The suite this joins asserted the exposed tool <em>set</em> exhaustively and then exercised
///         seven of them. The rest were covered by their name appearing in a list — and could not have
///         been covered by more, because the fixtures registered no saved-query, connector, or
///         query-planning services, so constructing those tools failed in the DI container. A tool
///         that has never been called is a tool whose authorization, paging, and error handling are
///         claims rather than facts.
///     </para>
///     <para>
///         Writes and operator commands are both enabled here. That is the whole surface, which is
///         what makes it the right place to assert the properties that must hold across all of it:
///         that every tool reports an output schema, that every parameter is described, and that a
///         domain failure comes back readable.
///     </para>
/// </remarks>
public sealed class McpToolCoverageTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-mcp-cover", Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _ownerToken = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Lakehold:Mcp:Enabled"] = "true",
            ["Lakehold:Mcp:AllowWrites"] = "true",
            ["Lakehold:Mcp:AllowOperatorCommands"] = "true",
        });
        builder.Services.Configure<LakeholdOidcOptions>(_ => { });
        builder.AddLakeholdMcpForTests(_root);

        _app = builder.Build();
        _app.UseRateLimiter();
        _app.MapLakeholdMcp();
        await _app.StartAsync();

        using var scope = _app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
        await context.Database.EnsureCreatedAsync();

        var demo = new Tenant { Slug = "demo", DisplayName = "Demo", CreatedUtc = DateTimeOffset.UtcNow };
        var other = new Tenant { Slug = "other", DisplayName = "Other", CreatedUtc = DateTimeOffset.UtcNow };
        context.Tenants.AddRange(demo, other);
        await context.SaveChangesAsync();

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

        // Owner, because the connector and maintenance tools declare TenantOwner.
        _ownerToken = Persist(context, ApiTokenFactory.Issue(
            TokenScope.Tenant, demo, "agent", DateTimeOffset.UtcNow, role: TokenRole.Owner));
        await context.SaveChangesAsync();

        var lakehouse = scope.ServiceProvider.GetRequiredService<LakehouseService>();
        await lakehouse.ExecuteAsync(
            "demo", "analytics", "CREATE TABLE facts (id INTEGER, label VARCHAR)",
            CancellationToken.None, readOnly: false);
        await lakehouse.ExecuteAsync(
            "demo", "analytics", "INSERT INTO facts VALUES (1, 'one'), (2, 'two'), (3, 'three')",
            CancellationToken.None, readOnly: false);
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

    // ---- properties that must hold across the whole surface ----

    [Fact]
    public async Task The_handshake_carries_instructions_and_an_identity()
    {
        // Clients put these in the model's system prompt. Absent, everything the server knows that is
        // not attached to one tool — call list_tenants first, ranges are inclusive, reads never write —
        // reaches the agent only if it happens to read the right description.
        await using var client = await ConnectAsync(_ownerToken);

        Assert.NotNull(client.ServerInstructions);
        Assert.Contains("list_tenants", client.ServerInstructions, StringComparison.Ordinal);
        Assert.Contains("INCLUSIVE at both ends", client.ServerInstructions, StringComparison.Ordinal);

        Assert.Equal("lakehold", client.ServerInfo.Name);
        Assert.False(string.IsNullOrWhiteSpace(client.ServerInfo.Version));

        // 1.0.0 is what an assembly reports when nobody set a version, and it was what this server
        // advertised on a 2.3.x release.
        Assert.NotEqual("1.0.0", client.ServerInfo.Version);
    }

    [Fact]
    public async Task Every_tool_reports_an_output_schema()
    {
        // Without one a client receives JSON as free text: it cannot validate the shape, and neither
        // can the model. The only exemptions are tools that return nothing at all.
        var returnsNothing = new[] { "delete_saved_query", "retire_connector" };
        await using var client = await ConnectAsync(_ownerToken);

        var tools = await client.ListToolsAsync();

        Assert.All(
            tools.Where(tool => !returnsNothing.Contains(tool.Name, StringComparer.Ordinal)),
            tool => Assert.True(
                tool.ProtocolTool.OutputSchema.HasValue,
                $"'{tool.Name}' returns a value but advertises no output schema."));
    }

    [Fact]
    public async Task Every_tool_parameter_is_described()
    {
        // An agent never sees a method signature, only this schema. An undescribed `schema`, `limit`,
        // or `revision` is a value it has to guess, and a confident wrong guess is the failure mode.
        await using var client = await ConnectAsync(_ownerToken);

        var undescribed = new List<string>();
        foreach (var tool in await client.ListToolsAsync())
        {
            if (!tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties))
            {
                continue;
            }

            foreach (var property in properties.EnumerateObject())
            {
                if (!property.Value.TryGetProperty("description", out var description)
                    || string.IsNullOrWhiteSpace(description.GetString()))
                {
                    undescribed.Add($"{tool.Name}.{property.Name}");
                }
            }
        }

        Assert.Empty(undescribed);
    }

    [Fact]
    public async Task A_domain_failure_comes_back_readable_rather_than_opaque()
    {
        // The SDK reports an uncaught exception as "An error occurred invoking 'x'." with the message
        // withheld, which is right for an unexpected one and useless for a mistake the caller made.
        await using var client = await ConnectAsync(_ownerToken);

        var result = await client.CallToolAsync(
            "get_column_distribution",
            Arguments(new() { ["table"] = "facts", ["column"] = "nonexistent" }));

        Assert.True(result.IsError);
        Assert.Contains("nonexistent", Text(result), StringComparison.Ordinal);
    }

    // ---- inspection ----

    [Fact]
    public async Task The_inspection_tools_read_the_physical_and_logical_layer()
    {
        await using var client = await ConnectAsync(_ownerToken);

        var storage = Payload(await client.CallToolAsync("get_storage", Arguments([])));
        Assert.Contains(
            storage.GetProperty("tables").EnumerateArray(),
            table => table.GetProperty("tableName").GetString() == "facts");

        var files = Payload(await client.CallToolAsync(
            "list_storage_files", Arguments(new() { ["table"] = "facts" })));
        Assert.True(files.TryGetProperty("files", out _));

        var detail = Payload(await client.CallToolAsync(
            "get_table_detail", Arguments(new() { ["table"] = "facts" })));
        Assert.Equal("facts", detail.GetProperty("tableName").GetString());

        var profile = Payload(await client.CallToolAsync(
            "get_table_profile", Arguments(new() { ["table"] = "facts" })));
        Assert.Contains(
            profile.GetProperty("columns").EnumerateArray(),
            column => column.GetProperty("name").GetString() == "label");

        var distribution = Payload(await client.CallToolAsync(
            "get_column_distribution", Arguments(new() { ["table"] = "facts", ["column"] = "id" })));
        Assert.Equal("id", distribution.GetProperty("columnName").GetString());
    }

    [Fact]
    public async Task Query_history_attributes_the_agents_own_statement()
    {
        await using var client = await ConnectAsync(_ownerToken);
        _ = await client.CallToolAsync("query", Arguments(new() { ["sql"] = "SELECT 42 AS answer" }));

        var history = Payload(await client.CallToolAsync("query_history", Arguments([])));

        var mine = history.EnumerateArray().First(run => run.GetProperty("sql").GetString()!.Contains("42"));
        Assert.Equal("Mcp", mine.GetProperty("origin").GetString());
        Assert.Equal("ApiToken", mine.GetProperty("actorKind").GetString());
        Assert.Equal("agent", mine.GetProperty("actorName").GetString());
    }

    // ---- snapshots ----

    [Fact]
    public async Task Get_snapshot_and_query_snapshot_read_a_retained_snapshot()
    {
        await using var client = await ConnectAsync(_ownerToken);
        var snapshots = Payload(await client.CallToolAsync("list_snapshots", Arguments([])));
        var newest = snapshots.EnumerateArray().First().GetProperty("snapshotId").GetInt64();

        var one = Payload(await client.CallToolAsync(
            "get_snapshot", Arguments(new() { ["snapshotId"] = newest })));
        Assert.Equal(newest, one.GetProperty("snapshotId").GetInt64());

        var preview = Payload(await client.CallToolAsync(
            "query_snapshot", Arguments(new() { ["snapshotId"] = newest, ["table"] = "facts" })));
        Assert.Equal(3, preview.GetProperty("rowCount").GetInt32());
    }

    [Fact]
    public async Task An_unretained_snapshot_is_refused_by_id()
    {
        await using var client = await ConnectAsync(_ownerToken);

        var result = await client.CallToolAsync(
            "get_snapshot", Arguments(new() { ["snapshotId"] = 9_999L }));

        Assert.True(result.IsError);
        Assert.Contains("9999", Text(result), StringComparison.Ordinal);
    }

    // ---- saved queries ----

    [Fact]
    public async Task The_saved_query_lifecycle_runs_end_to_end()
    {
        await using var client = await ConnectAsync(_ownerToken);

        var created = Payload(await client.CallToolAsync("create_saved_query", Arguments(new()
        {
            ["name"] = "labelled facts",
            ["source"] = "SELECT label FROM facts ORDER BY id",
        })));
        var id = created.GetProperty("id").GetInt32();
        Assert.Equal(1, created.GetProperty("revision").GetInt32());

        var listed = Payload(await client.CallToolAsync("list_saved_queries", Arguments([])));
        Assert.Contains(listed.EnumerateArray(), q => q.GetProperty("id").GetInt32() == id);

        var fetched = Payload(await client.CallToolAsync("get_saved_query", Arguments(new() { ["id"] = id })));
        Assert.Equal("labelled facts", fetched.GetProperty("name").GetString());

        var executed = Payload(await client.CallToolAsync("execute_saved_query", Arguments(new() { ["id"] = id })));
        Assert.Equal(3, executed.GetProperty("rows").GetArrayLength());
        Assert.Contains("facts", executed.GetProperty("generatedSql").GetString()!, StringComparison.Ordinal);

        var updated = Payload(await client.CallToolAsync("update_saved_query", Arguments(new()
        {
            ["id"] = id,
            ["revision"] = 1,
            ["name"] = "labelled facts",
            ["source"] = "SELECT label FROM facts WHERE id = 1",
        })));
        Assert.Equal(2, updated.GetProperty("revision").GetInt32());

        var published = Payload(await client.CallToolAsync("publish_saved_query", Arguments(new()
        {
            ["id"] = id,
            ["revision"] = 2,
            ["viewName"] = "one_fact",
        })));
        Assert.Equal("one_fact", published.GetProperty("publishedViewName").GetString());

        // Published means selectable as an ordinary catalog view, which is the whole point — and
        // reading it back through `query` exercises the read-only attachment, which is where a
        // publish used to become invisible to the agent that had just made it.
        var throughTheView = Payload(await client.CallToolAsync(
            "query", Arguments(new() { ["sql"] = "SELECT * FROM one_fact" })));
        Assert.Equal(1, throughTheView.GetProperty("rowCount").GetInt32());

        var unpublished = Payload(await client.CallToolAsync("unpublish_saved_query", Arguments(new()
        {
            ["id"] = id,
            ["revision"] = published.GetProperty("revision").GetInt32(),
        })));
        // Null properties are omitted from structured content, so absence is how "not published" reads.
        Assert.Null(Optional(unpublished, "publishedViewName"));

        var deleted = await client.CallToolAsync("delete_saved_query", Arguments(new()
        {
            ["id"] = id,
            ["revision"] = unpublished.GetProperty("revision").GetInt32(),
        }));
        Assert.NotEqual(true, deleted.IsError);

        var afterDelete = Payload(await client.CallToolAsync("list_saved_queries", Arguments([])));
        Assert.DoesNotContain(afterDelete.EnumerateArray(), q => q.GetProperty("id").GetInt32() == id);
    }

    [Fact]
    public async Task A_stale_saved_query_revision_is_refused_with_a_readable_conflict()
    {
        await using var client = await ConnectAsync(_ownerToken);
        var created = Payload(await client.CallToolAsync("create_saved_query", Arguments(new()
        {
            ["name"] = "stale test",
            ["source"] = "SELECT 1",
        })));
        var id = created.GetProperty("id").GetInt32();

        var conflict = await client.CallToolAsync("update_saved_query", Arguments(new()
        {
            ["id"] = id,
            ["revision"] = 99,
            ["name"] = "stale test",
            ["source"] = "SELECT 2",
        }));

        Assert.True(conflict.IsError);
        Assert.False(
            Text(conflict).EndsWith("update_saved_query'.", StringComparison.Ordinal),
            "A stale revision must explain itself, not fall through to the SDK's opaque message.");
    }

    // ---- query languages ----

    [Fact]
    public async Task Query_languages_are_discoverable_including_an_unavailable_one()
    {
        await using var client = await ConnectAsync(_ownerToken);

        var languages = Payload(await client.CallToolAsync("list_query_languages", Arguments([])))
            .EnumerateArray()
            .ToDictionary(l => l.GetProperty("id").GetString()!);

        Assert.True(languages.ContainsKey("sql"));

        // Reported rather than omitted: an agent told "linq exists but its compiler is unreachable"
        // stops guessing at ids, where one shown a list without it concludes it never existed.
        var unavailable = languages[StubQuerySourcePlanner.UnavailableLanguage];
        Assert.False(unavailable.GetProperty("available").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(unavailable.GetProperty("unavailableReason").GetString()));
    }

    [Fact]
    public async Task A_non_sql_query_runs_and_reports_the_sql_it_compiled_to()
    {
        await using var client = await ConnectAsync(_ownerToken);

        var result = Payload(await client.CallToolAsync("query_language", Arguments(new()
        {
            ["language"] = StubQuerySourcePlanner.PlannableLanguage,
            ["source"] = "SELECT count(*) AS n FROM facts",
        })));

        Assert.Equal(1, result.GetProperty("rowCount").GetInt32());
        Assert.Contains("facts", result.GetProperty("generatedSql").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unavailable_language_says_so_rather_than_failing_opaquely()
    {
        await using var client = await ConnectAsync(_ownerToken);

        var result = await client.CallToolAsync("query_language", Arguments(new()
        {
            ["language"] = StubQuerySourcePlanner.UnavailableLanguage,
            ["source"] = "from f in facts select f",
        }));

        Assert.True(result.IsError);
        Assert.Contains(
            StubQuerySourcePlanner.UnavailableLanguage, Text(result), StringComparison.OrdinalIgnoreCase);
    }

    // ---- table restore ----

    [Fact]
    public async Task A_table_is_restored_from_a_snapshot_through_plan_then_apply()
    {
        await using var client = await ConnectAsync(_ownerToken);
        var before = Payload(await client.CallToolAsync("list_snapshots", Arguments([])))
            .EnumerateArray().First().GetProperty("snapshotId").GetInt64();

        _ = await client.CallToolAsync("execute", Arguments(new() { ["sql"] = "DELETE FROM facts WHERE id > 1" }));
        Assert.Equal(
            1,
            Payload(await client.CallToolAsync("query", Arguments(new() { ["sql"] = "SELECT * FROM facts" })))
                .GetProperty("rowCount").GetInt32());

        var plan = Payload(await client.CallToolAsync("plan_table_restore", Arguments(new()
        {
            ["table"] = "facts",
            ["snapshotId"] = before,
        })));
        Assert.Equal(1L, plan.GetProperty("currentRowCount").GetInt64());
        Assert.Equal(3L, plan.GetProperty("historicalRowCount").GetInt64());

        var applied = Payload(await client.CallToolAsync("apply_table_restore", Arguments(new()
        {
            ["table"] = "facts",
            ["snapshotId"] = before,
            ["currentSnapshotId"] = plan.GetProperty("currentSnapshotId").GetInt64(),
        })));
        Assert.Equal(3L, applied.GetProperty("restoredRowCount").GetInt64());

        // Proven by reading the table back, not by trusting the response.
        Assert.Equal(
            3,
            Payload(await client.CallToolAsync("query", Arguments(new() { ["sql"] = "SELECT * FROM facts" })))
                .GetProperty("rowCount").GetInt32());
    }

    [Fact]
    public async Task A_restore_fenced_on_a_stale_snapshot_is_refused()
    {
        // The fence is the whole safety property: a restore decided against one state of the table
        // and applied against another is silent data loss.
        await using var client = await ConnectAsync(_ownerToken);
        var target = Payload(await client.CallToolAsync("list_snapshots", Arguments([])))
            .EnumerateArray().First().GetProperty("snapshotId").GetInt64();

        var plan = Payload(await client.CallToolAsync("plan_table_restore", Arguments(new()
        {
            ["table"] = "facts",
            ["snapshotId"] = target,
        })));

        // Somebody else commits between the plan and the apply.
        _ = await client.CallToolAsync("execute", Arguments(new() { ["sql"] = "INSERT INTO facts VALUES (9, 'nine')" }));

        var refused = await client.CallToolAsync("apply_table_restore", Arguments(new()
        {
            ["table"] = "facts",
            ["snapshotId"] = target,
            ["currentSnapshotId"] = plan.GetProperty("currentSnapshotId").GetInt64(),
        }));

        Assert.True(refused.IsError);
        Assert.Contains("advanced", Text(refused), StringComparison.OrdinalIgnoreCase);

        // And the interloping row survived, so the refusal really did change nothing.
        Assert.Equal(
            4,
            Payload(await client.CallToolAsync("query", Arguments(new() { ["sql"] = "SELECT * FROM facts" })))
                .GetProperty("rowCount").GetInt32());
    }

    // ---- maintenance ----

    [Fact]
    public async Task Maintenance_plans_then_applies_against_the_snapshot_it_reviewed()
    {
        await using var client = await ConnectAsync(_ownerToken);

        var plan = Payload(await client.CallToolAsync("plan_maintenance", Arguments(new() { ["operation"] = "flush" })));
        Assert.Equal("flush", plan.GetProperty("operation").GetString());

        var applied = Payload(await client.CallToolAsync("apply_maintenance", Arguments(new()
        {
            ["operation"] = "flush",
            ["currentSnapshotId"] = plan.GetProperty("currentSnapshotId").GetInt64(),
        })));
        Assert.Equal("flush", applied.GetProperty("operation").GetString());
    }

    [Fact]
    public async Task Maintenance_refuses_a_stale_plan_and_an_unknown_operation()
    {
        await using var client = await ConnectAsync(_ownerToken);

        var stale = await client.CallToolAsync("apply_maintenance", Arguments(new()
        {
            ["operation"] = "compact",
            ["currentSnapshotId"] = 1L,
        }));
        Assert.True(stale.IsError);

        var unknown = await client.CallToolAsync("plan_maintenance", Arguments(new() { ["operation"] = "vacuum" }));
        Assert.True(unknown.IsError);
        Assert.Contains("flush, compact, expire, or cleanup", Text(unknown), StringComparison.Ordinal);
    }

    // ---- connectors ----

    [Fact]
    public async Task The_connector_control_plane_runs_end_to_end()
    {
        await using var client = await ConnectAsync(_ownerToken);

        var valid = Payload(await client.CallToolAsync(
            "validate_connector", Arguments(new() { ["definition"] = Definition("orders") })));
        Assert.True(valid.GetProperty("valid").GetBoolean(), Optional(valid, "error"));

        var created = Payload(await client.CallToolAsync(
            "create_connector", Arguments(new() { ["definition"] = Definition("orders") })));
        var id = created.GetProperty("id").GetInt32();

        var listed = Payload(await client.CallToolAsync("list_connectors", Arguments([])));
        Assert.Contains(listed.EnumerateArray(), c => c.GetProperty("id").GetInt32() == id);

        var fetched = Payload(await client.CallToolAsync("get_connector", Arguments(new() { ["id"] = id })));
        Assert.Equal("orders", fetched.GetProperty("name").GetString());

        var paused = Payload(await client.CallToolAsync("pause_connector", Arguments(new()
        {
            ["id"] = id,
            ["version"] = fetched.GetProperty("version").GetInt32(),
        })));
        var resumed = Payload(await client.CallToolAsync("resume_connector", Arguments(new()
        {
            ["id"] = id,
            ["version"] = paused.GetProperty("version").GetInt32(),
        })));

        var runs = Payload(await client.CallToolAsync("list_connector_runs", Arguments(new() { ["id"] = id })));
        Assert.Equal(0, runs.GetArrayLength());
        var dead = Payload(await client.CallToolAsync("list_connector_dead_letters", Arguments(new() { ["id"] = id })));
        Assert.Equal(0, dead.GetArrayLength());

        var updated = Payload(await client.CallToolAsync("update_connector", Arguments(new()
        {
            ["id"] = id,
            ["version"] = resumed.GetProperty("version").GetInt32(),
            ["definition"] = Definition("orders", description: "renamed"),
        })));

        var retired = await client.CallToolAsync("retire_connector", Arguments(new()
        {
            ["id"] = id,
            ["version"] = updated.GetProperty("version").GetInt32(),
        }));
        Assert.NotEqual(true, retired.IsError);
    }

    [Fact]
    public async Task A_connector_naming_a_host_outside_the_egress_policy_is_refused_with_the_reason()
    {
        await using var client = await ConnectAsync(_ownerToken);

        var validation = Payload(await client.CallToolAsync("validate_connector", Arguments(new()
        {
            ["definition"] = Definition("elsewhere", host: "not-allowed.example.com"),
        })));

        Assert.False(validation.GetProperty("valid").GetBoolean());
        Assert.Contains("egress policy", Optional(validation, "error")!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connector_the_administration_ui_would_refuse_cannot_be_created_through_MCP()
    {
        // Both surfaces share DataConnectorEndpoints.ValidateAsync precisely so this holds.
        await using var client = await ConnectAsync(_ownerToken);

        var refused = await client.CallToolAsync("create_connector", Arguments(new()
        {
            ["definition"] = Definition("elsewhere", host: "not-allowed.example.com"),
        }));

        Assert.True(refused.IsError);
        Assert.Contains("egress policy", Text(refused), StringComparison.Ordinal);
    }

    // ---- resources and completion ----

    [Fact]
    public async Task The_snapshot_resource_returns_one_snapshot_and_refuses_another_tenant()
    {
        await using var client = await ConnectAsync(_ownerToken);
        var newest = Payload(await client.CallToolAsync("list_snapshots", Arguments([])))
            .EnumerateArray().First().GetProperty("snapshotId").GetInt64();

        var read = await client.ReadResourceAsync($"lakehold://demo/analytics/snapshots/{newest}");
        var body = JsonDocument.Parse(
            Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text).RootElement;
        Assert.Equal(newest, body.GetProperty("snapshotId").GetInt64());

        await Assert.ThrowsAnyAsync<Exception>(
            async () => await client.ReadResourceAsync("lakehold://other/anything/snapshots/1"));
    }

    [Fact]
    public async Task Completion_suggests_only_what_the_credential_can_reach()
    {
        await using var client = await ConnectAsync(_ownerToken);

        var tenants = await CompleteAsync(client, SchemaTemplate, "tenant", []);
        Assert.Equal(["demo"], tenants);

        var catalogs = await CompleteAsync(
            client, SchemaTemplate, "catalog", new() { ["tenant"] = "demo" });
        Assert.Equal(["analytics"], catalogs);

        var snapshots = await CompleteAsync(
            client, SnapshotTemplate, "snapshotId",
            new() { ["tenant"] = "demo", ["catalog"] = "analytics" });
        Assert.NotEmpty(snapshots);
    }

    [Fact]
    public async Task Completion_for_an_unreachable_tenant_suggests_nothing_rather_than_refusing()
    {
        // A refusal here would answer "does that tenant exist?" through a side channel the tools are
        // careful to close (invariant 19).
        await using var client = await ConnectAsync(_ownerToken);

        var catalogs = await CompleteAsync(
            client, SchemaTemplate, "catalog", new() { ["tenant"] = "other" });

        Assert.Empty(catalogs);
    }

    // ---- read-after-write across attachment modes ----

    [Fact]
    public async Task A_read_sees_a_write_made_through_the_execute_tool()
    {
        // This is the surface's own shape, not an edge case: `query` always attaches read-only and
        // `execute` never does, so an agent that writes and reads back is always using two pooled
        // sessions (invariant 20). A DuckLake catalog attached read-only answers from the snapshot it
        // attached at, so before the writer invalidated the reader this returned the pre-write rows —
        // silently, with no error and no truncation flag, for as long as the session stayed warm.
        await using var client = await ConnectAsync(_ownerToken);

        // Warm the read-only session first. Without this the reader attaches after the write and the
        // test passes for the wrong reason.
        Assert.Equal(
            3,
            Payload(await client.CallToolAsync("query", Arguments(new() { ["sql"] = "SELECT * FROM facts" })))
                .GetProperty("rowCount").GetInt32());

        _ = Payload(await client.CallToolAsync(
            "execute", Arguments(new() { ["sql"] = "INSERT INTO facts VALUES (4, 'four')" })));

        Assert.Equal(
            4,
            Payload(await client.CallToolAsync("query", Arguments(new() { ["sql"] = "SELECT * FROM facts" })))
                .GetProperty("rowCount").GetInt32());
    }

    [Fact]
    public async Task A_read_sees_DDL_made_through_the_execute_tool()
    {
        // The same failure with a schema change rather than rows, which is how it reaches an agent
        // that publishes a saved query and then cannot select from the view it just created.
        await using var client = await ConnectAsync(_ownerToken);
        _ = Payload(await client.CallToolAsync("query", Arguments(new() { ["sql"] = "SELECT * FROM facts" })));

        _ = Payload(await client.CallToolAsync(
            "execute", Arguments(new() { ["sql"] = "CREATE VIEW recent AS SELECT * FROM facts WHERE id > 1" })));

        var throughTheView = Payload(await client.CallToolAsync(
            "query", Arguments(new() { ["sql"] = "SELECT * FROM recent" })));
        Assert.Equal(2, throughTheView.GetProperty("rowCount").GetInt32());
    }

    // ---- transport behaviour ----

    [Fact]
    public async Task A_cancelled_call_does_not_leave_the_session_unusable()
    {
        // Cancellation is threaded from the transport through the engine. What matters to a client is
        // that abandoning one call leaves the next one working rather than wedging the session.
        await using var client = await ConnectAsync(_ownerToken);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.CallToolAsync(
                "query",
                Arguments(new() { ["sql"] = "SELECT 1" }),
                cancellationToken: cancelled.Token));

        var after = Payload(await client.CallToolAsync("query", Arguments(new() { ["sql"] = "SELECT 7 AS n" })));
        Assert.Equal(1, after.GetProperty("rowCount").GetInt32());
    }

    // ---- helpers ----

    private const string SchemaTemplate = "lakehold://{tenant}/{catalog}/schema";
    private const string SnapshotTemplate = "lakehold://{tenant}/{catalog}/snapshots/{snapshotId}";

    private static async Task<IList<string>> CompleteAsync(
        McpClient client,
        string template,
        string argument,
        Dictionary<string, string> known)
    {
        var result = await client.CompleteAsync(new CompleteRequestParams
        {
            Ref = new ResourceTemplateReference { Uri = template },
            Argument = new Argument { Name = argument, Value = string.Empty },
            Context = known.Count == 0 ? null : new CompleteContext { Arguments = known },
        });

        return result.Completion.Values;
    }

    private static object Definition(string name, string host = McpTestHost.AllowedConnectorHost, string? description = null)
        => new
        {
            name,
            description,
            owner = "data-team",
            kind = "rest",
            endpointUrl = $"https://{host}/records",
            targetSchema = "main",
            targetTable = name,
            minimumRows = 1,
        };

    private static Dictionary<string, object?> Arguments(Dictionary<string, object?> extra)
    {
        var arguments = new Dictionary<string, object?> { ["tenant"] = "demo", ["catalog"] = "analytics" };
        foreach (var (key, value) in extra)
        {
            arguments[key] = value;
        }

        return arguments;
    }

    /// <summary>A property that may be absent, because null members are omitted from structured content.</summary>
    private static string? Optional(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static JsonElement Payload(CallToolResult result)
    {
        // `is not true`, because IsError is a bool? that stays null on success. The message matters:
        // a failing lifecycle test is useless if it only says "true was not false" about a call five
        // steps in.
        Assert.True(result.IsError is not true, Text(result));

        // StructuredContent, not the text fallback: reading it here is what keeps the output-schema
        // work honest, because a regression that drops structured content fails these tests too.
        return result.StructuredContent ?? throw new InvalidOperationException(
            "The tool returned no structured content: " + Text(result));
    }

    private static string Text(CallToolResult result)
        => string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private static string Persist(ControlPlaneContext context, IssuedToken issued)
    {
        context.ApiTokens.Add(issued.Record);
        return issued.Plaintext;
    }

    private async Task<McpClient> ConnectAsync(string bearer)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + bearer },
            },
            _app.GetTestClient());

        return await McpClient.CreateAsync(transport);
    }
}
