using System.Net;
using System.Text.Json;
using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api.Auth;
using Lakehold.Api.Mcp;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     OAuth 2.0 Protected Resource Metadata (RFC 9728), which the 2026-07-28 MCP revision requires so
///     a client with no credential can discover where to get one.
/// </summary>
/// <remarks>
///     The document is only served where OIDC is configured. Absent an authority there is nothing to
///     name, and a document advertising no authorization server would be worse than none: a client
///     would discover it, learn nothing, and fail somewhere less obvious.
/// </remarks>
public sealed class McpResourceMetadataTests
{
    [Fact]
    public async Task The_document_is_served_and_names_the_authority_and_the_resource()
    {
        await using var app = await StartAsync(authority: "https://idp.example.com/realms/lakehold");
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri(McpResourceMetadata.Path, UriKind.Relative));
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        // Field names are the spec's because the SDK's own ProtectedResourceMetadata type is what gets
        // serialised — hand-written JSON keys would be ours, and wrong the first time the spec moved.
        Assert.Equal(
            "https://idp.example.com/realms/lakehold",
            root.GetProperty("authorization_servers")[0].GetString());
        Assert.EndsWith("/mcp", root.GetProperty("resource").GetString()!, StringComparison.Ordinal);
        Assert.Equal("header", root.GetProperty("bearer_methods_supported")[0].GetString());
    }

    [Fact]
    public async Task The_document_needs_no_credential_to_read()
    {
        // A client reads this *because* it has no credential yet, so requiring one would be circular.
        // It names an issuer and a resource — both already public — and no tenant.
        await using var app = await StartAsync(authority: "https://idp.example.com");
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri(McpResourceMetadata.Path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Nothing_is_served_where_OIDC_is_not_configured()
    {
        await using var app = await StartAsync(authority: "");
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri(McpResourceMetadata.Path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_unauthenticated_call_is_challenged_towards_the_document()
    {
        // The half that makes discovery work: the 401 has to say where the metadata is, or a client
        // has nothing to follow.
        await using var app = await StartAsync(authority: "https://idp.example.com");
        using var client = app.GetTestClient();

        using var response = await client.PostAsync(new Uri("/mcp", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var challenge = response.Headers.WwwAuthenticate.ToString();
        Assert.Contains("resource_metadata", challenge, StringComparison.Ordinal);
        Assert.Contains(McpResourceMetadata.Path, challenge, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_challenge_cites_nothing_where_there_is_nothing_to_cite()
    {
        // Without an authority the bare Bearer challenge is the honest answer: API tokens are the only
        // credential, and pointing at a document that is not served would send a client nowhere.
        await using var app = await StartAsync(authority: "");
        using var client = app.GetTestClient();

        using var response = await client.PostAsync(new Uri("/mcp", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(
            "resource_metadata", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_declared_public_base_url_overrides_what_the_request_saw()
    {
        // The production topology runs the API unpublished behind nginx, so the request describes the
        // internal hop. A client compares `resource` against the URL it called and follows
        // `resource_metadata`; both have to be the address it can actually reach, and only the operator
        // knows what that is.
        await using var app = await StartAsync(
            authority: "https://idp.example.com", publicBaseUrl: "https://lakehold.example.com");
        using var client = app.GetTestClient();

        using var document = await client.GetAsync(new Uri(McpResourceMetadata.Path, UriKind.Relative));
        using var json = JsonDocument.Parse(await document.Content.ReadAsStringAsync());

        Assert.Equal("https://lakehold.example.com/mcp", json.RootElement.GetProperty("resource").GetString());

        using var challenged = await client.PostAsync(new Uri("/mcp", UriKind.Relative), content: null);

        Assert.Contains(
            "https://lakehold.example.com/.well-known/oauth-protected-resource",
            challenged.Headers.WwwAuthenticate.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_saved_public_base_url_applies_without_restarting()
    {
        await using var app = await StartAsync(authority: "https://idp.example.com");

        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<McpRuntimeSettingsStore>()
                .SaveAsync(
                    enabled: true,
                    allowWrites: false,
                    maxRowsPerResult: 200,
                    publicBaseUrl: "https://new.example.com",
                    expectedVersion: 0,
                    CancellationToken.None);
        }

        using var client = app.GetTestClient();
        using var response = await client.GetAsync(new Uri(McpResourceMetadata.Path, UriKind.Relative));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("https://new.example.com/mcp", json.RootElement.GetProperty("resource").GetString());
    }

    private static async Task<WebApplication> StartAsync(string authority, string publicBaseUrl = "")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var controlPlanePath = Path.Combine(
            Path.GetTempPath(),
            $"lakehold-mcp-metadata-{Guid.NewGuid():N}.duckdb");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Lakehold:Mcp:Enabled"] = "true",
            ["Lakehold:Mcp:PublicBaseUrl"] = publicBaseUrl,
        });

        // Only the MCP surface and its options: the document and the challenge are decided before any
        // credential is resolved, so no control plane is needed to exercise either.
        builder.Services.Configure<McpOptions>(builder.Configuration.GetSection(McpOptions.SectionName));
        builder.Services.Configure<LakeholdOidcOptions>(o => o.Authority = authority);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddDbContext<ControlPlaneContext>(options =>
            options.UseDuckDB($"Data Source={controlPlanePath}"));
        builder.Services.AddScoped<MemberDirectory>();
        builder.AddLakeholdMcp();

        var app = builder.Build();
        app.MapLakeholdMcp();
        app.MapMcpResourceMetadata();
        await app.StartAsync();
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ControlPlaneContext>().Database.EnsureCreatedAsync();
        return app;
    }
}
