using System.Text.Json;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Keeps the bundled development realm and the configuration that reads it in step.
/// </summary>
/// <remarks>
///     These are two files nobody edits together. Renaming a claim in the realm without changing
///     <c>compose.yaml</c> produces a login that succeeds at the identity provider and then lands
///     back on the sign-in panel with no error anywhere — the single most confusing failure this
///     surface has, and one that costs an afternoon to diagnose by hand. It costs a millisecond to
///     catch here.
/// </remarks>
public sealed class RealmClaimContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Theory]
    // claim name emitted by the realm, and the compose default that must read it
    [InlineData("tenant", "Lakehold__Oidc__TenantClaim", "tenant")]
    [InlineData("role", "Lakehold__Oidc__RoleClaim", "role")]
    [InlineData("groups", "Lakehold__Oidc__SystemAdminClaim", "groups")]
    public void Every_claim_the_realm_emits_is_the_claim_the_stack_reads(
        string claimName,
        string settingKey,
        string expectedDefault)
    {
        Assert.Contains(claimName, RealmClaimNames());

        // ${VAR:-default} — the default is what a developer who sets nothing actually gets.
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot, "compose.yaml"));
        var line = compose
            .Split('\n')
            .FirstOrDefault(l => l.Contains(settingKey, StringComparison.Ordinal));

        Assert.NotNull(line);
        Assert.Contains($":-{expectedDefault}}}", line, StringComparison.Ordinal);
        Assert.Equal(expectedDefault, claimName);
    }

    [Fact]
    public void The_administrator_group_the_realm_defines_is_the_value_the_stack_matches()
    {
        using var realm = JsonDocument.Parse(File.ReadAllText(RealmPath()));
        var groups = realm.RootElement.GetProperty("groups")
            .EnumerateArray()
            .Select(g => g.GetProperty("name").GetString())
            .ToArray();

        var compose = File.ReadAllText(Path.Combine(RepositoryRoot, "compose.yaml"));
        var line = compose
            .Split('\n')
            .First(l => l.Contains("Lakehold__Oidc__SystemAdminValue", StringComparison.Ordinal));

        // A membership claim only grants administration when its value matches exactly. Seeding a
        // user into a group the stack does not recognise silently produces a non-administrator.
        Assert.Contains("lakehold-administrators", groups);
        Assert.Contains(":-lakehold-administrators}", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_client_the_realm_registers_is_the_client_the_stack_presents()
    {
        using var realm = JsonDocument.Parse(File.ReadAllText(RealmPath()));
        var client = Client(realm, "lakehold-workbench");
        var clientId = client.GetProperty("clientId").GetString();

        var compose = File.ReadAllText(Path.Combine(RepositoryRoot, "compose.yaml"));

        Assert.Equal("lakehold-workbench", clientId);
        Assert.Contains($":-{clientId}}}", compose, StringComparison.Ordinal);

        // Audience validation is only meaningful if the realm actually puts the client in the
        // audience. Without this mapper the setting below would have to be empty, which accepts
        // every token the realm ever issued, including one minted for a different application.
        var mappers = client.GetProperty("protocolMappers")
            .EnumerateArray()
            .Select(m => m.GetProperty("protocolMapper").GetString())
            .ToArray();
        Assert.Contains("oidc-audience-mapper", mappers);
        Assert.Contains($"Lakehold__Oidc__Audience: ${{LAKEHOLD_OIDC_AUDIENCE:-{clientId}}}", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void The_redirect_uri_the_realm_allows_is_the_one_the_workbench_uses()
    {
        using var realm = JsonDocument.Parse(File.ReadAllText(RealmPath()));
        var redirects = Client(realm, "lakehold-workbench")
            .GetProperty("redirectUris")
            .EnumerateArray()
            .Select(u => u.GetString())
            .ToArray();

        // BrowserAuthentication fixes the callback path; a realm that allows a different one turns
        // a working login into an invalid_redirect_uri at the provider.
        Assert.All(redirects, uri => Assert.EndsWith("/auth/callback", uri!, StringComparison.Ordinal));
        Assert.Contains(redirects, uri => uri!.Contains(":5399", StringComparison.Ordinal));
    }

    [Fact]
    public void The_mcp_client_is_public_pkce_only_and_targets_the_mcp_resource()
    {
        using var realm = JsonDocument.Parse(File.ReadAllText(RealmPath()));
        var client = Client(realm, "lakehold-mcp");

        Assert.True(client.GetProperty("publicClient").GetBoolean());
        Assert.Equal(
            "S256",
            client.GetProperty("attributes").GetProperty("pkce.code.challenge.method").GetString());
        var redirects = client.GetProperty("redirectUris")
            .EnumerateArray()
            .Select(redirect => redirect.GetString())
            .ToArray();
        Assert.Contains("http://127.0.0.1:*", redirects);
        Assert.Contains("http://localhost:*", redirects);

        var audience = client.GetProperty("protocolMappers")
            .EnumerateArray()
            .Single(mapper => mapper.GetProperty("protocolMapper").GetString() == "oidc-audience-mapper")
            .GetProperty("config")
            .GetProperty("included.custom.audience")
            .GetString();
        Assert.Equal("http://localhost:5399/mcp", audience);

        var compose = File.ReadAllText(Path.Combine(RepositoryRoot, "compose.yaml"));
        Assert.Contains("Lakehold__Oidc__McpClientId: ${LAKEHOLD_OIDC_MCP_CLIENT_ID:-lakehold-mcp}", compose, StringComparison.Ordinal);
    }

    private static string[] RealmClaimNames()
    {
        using var realm = JsonDocument.Parse(File.ReadAllText(RealmPath()));
        return Client(realm, "lakehold-workbench")
            .GetProperty("protocolMappers")
            .EnumerateArray()
            .Where(m => m.GetProperty("config").TryGetProperty("claim.name", out _))
            .Select(m => m.GetProperty("config").GetProperty("claim.name").GetString()!)
            .ToArray();
    }

    private static JsonElement Client(JsonDocument realm, string clientId) =>
        realm.RootElement.GetProperty("clients")
            .EnumerateArray()
            .Single(client => client.GetProperty("clientId").GetString() == clientId);

    private static string RealmPath()
        => Path.Combine(RepositoryRoot, "deploy", "keycloak", "lakehold-realm.json");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "compose.yaml")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
