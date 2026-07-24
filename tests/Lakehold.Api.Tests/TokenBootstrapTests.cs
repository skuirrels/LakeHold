using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Cover for the bootstrap credential: a node with no tokens mints exactly one instance-scoped
///     token, an override is honoured, and a node that already has a token is left alone — the
///     property that stops bootstrap minting a second admin credential on a running deployment.
/// </summary>
public sealed class TokenBootstrapTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-bootstrap", Guid.NewGuid().ToString("N"));
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddDbContext<ControlPlaneContext>(o => o.UseDuckDB($"Data Source={Path.Combine(_root, "cp.duckdb")}"));
        _services = collection.BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
        await context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
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
    public async Task An_empty_node_mints_one_instance_token()
    {
        await TokenBootstrap.EnsureBootstrapTokenAsync(_services, overrideToken: null, NullLogger(), TimeProvider.System);

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();

        var token = await context.ApiTokens.SingleAsync();
        Assert.Equal(TokenScope.Instance, token.Scope);
        Assert.Null(token.TenantId);
        Assert.Equal("lkh_admin_", token.Prefix);
    }

    [Fact]
    public async Task An_override_token_is_stored_by_its_hash()
    {
        // A well-formed instance token the operator chose out of band.
        var chosen = ApiTokenFactory.Issue(TokenScope.Instance, tenant: null, "external", DateTimeOffset.UtcNow).Plaintext;

        await TokenBootstrap.EnsureBootstrapTokenAsync(_services, chosen, NullLogger(), TimeProvider.System);

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
        var token = await context.ApiTokens.SingleAsync();

        Assert.Equal(TokenScope.Instance, token.Scope);
        Assert.Equal(ApiTokenFactory.Hash(chosen), token.SecretHash);
        Assert.NotEqual(chosen, token.SecretHash);
    }

    [Fact]
    public async Task A_malformed_override_falls_back_to_minting()
    {
        await TokenBootstrap.EnsureBootstrapTokenAsync(_services, "not-a-real-token", NullLogger(), TimeProvider.System);

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
        var token = await context.ApiTokens.SingleAsync();

        Assert.Equal(TokenScope.Instance, token.Scope);
        Assert.Equal("lkh_admin_", token.Prefix);
    }

    [Fact]
    public async Task A_node_that_already_has_a_token_is_left_alone()
    {
        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
            context.ApiTokens.Add(ApiTokenFactory.Issue(TokenScope.Instance, tenant: null, "existing", DateTimeOffset.UtcNow).Record);
            await context.SaveChangesAsync();
        }

        await TokenBootstrap.EnsureBootstrapTokenAsync(_services, overrideToken: null, NullLogger(), TimeProvider.System);

        await using var read = _services.CreateAsyncScope();
        var final = read.ServiceProvider.GetRequiredService<ControlPlaneContext>();
        Assert.Equal(1, await final.ApiTokens.CountAsync());
    }

    private ILogger NullLogger() =>
        _services.GetRequiredService<ILoggerFactory>().CreateLogger("test");
}
