using System.Net;
using System.Net.Http.Json;
using Lakehold.Querying;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Linq.Compiler.Tests;

public sealed class LinqCompilerHttpTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public LinqCompilerHttpTests(WebApplicationFactory<Program> factory)
        => _client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    // GitHub runs every test assembly concurrently. Give real HTTP cold starts
                    // headroom without weakening the production default or the hard-deadline test.
                    ["Lakehold:LinqCompiler:Timeout"] = "00:00:30",
                }));
        }).CreateClient();

    [Fact]
    public async Task Real_http_surface_describes_and_plans_linq()
    {
        var descriptor = await _client.GetFromJsonAsync<QueryLanguageDescriptor>("/descriptor");
        Assert.Equal("csharp-linq", descriptor?.Id);

        using var response = await _client.PostAsJsonAsync("/plan", Request("Main.Events.Take(2)"));
        response.EnsureSuccessStatusCode();
        var plan = await response.Content.ReadFromJsonAsync<QueryPlan>();
        Assert.NotNull(plan);
        Assert.Contains("LIMIT", plan.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("schema-1", plan.SchemaFingerprint);
    }

    [Fact]
    public async Task Hostile_source_is_rejected_with_editor_diagnostics()
    {
        using var response = await _client.PostAsJsonAsync(
            "/plan",
            Request("System.Diagnostics.Process.GetProcesses().AsQueryable()"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var failure = await response.Content.ReadFromJsonAsync<QueryPlanningFailure>();
        Assert.NotEmpty(failure?.Diagnostics ?? []);
    }

    [Fact]
    public async Task Disposable_worker_is_terminated_at_the_hard_deadline()
    {
        var process = new LinqCompilerProcess(Options.Create(new LinqCompilerOptions
        {
            Timeout = TimeSpan.FromMilliseconds(1),
        }));

        await Assert.ThrowsAsync<TimeoutException>(() => process.CompileAsync(
            Request("Main.Events.Take(2)"),
            default));
    }

    [Fact]
    public async Task Repeated_requests_do_not_leak_worker_state()
    {
        for (var index = 0; index < 12; index++)
        {
            using var response = await _client.PostAsJsonAsync(
                "/plan",
                Request($"Main.Events.Where(e => e.Id > {index}).Take(1)"));
            response.EnsureSuccessStatusCode();
        }
    }

    private static QueryPlanningRequest Request(string source)
        => new(
            source,
            "schema-1",
            [new QueryTableSchema(
                "main",
                "events",
                "TABLE",
                [
                    new QueryColumnSchema("id", "INTEGER", false),
                    new QueryColumnSchema("country", "VARCHAR", false),
                ])]);
}
