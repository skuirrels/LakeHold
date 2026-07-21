using System.Net;
using System.Net.Sockets;
using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api.PgWire;
using Lakehold.ControlPlane.Data;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Cover for the passwordless path, which <see cref="PgWireEndpointTests"/> never takes.
/// </summary>
/// <remarks>
///     This exists because of a bug it would have caught: the handshake sent
///     <c>AuthenticationOk</c> twice when no password was configured — once from the credential check
///     and once from the caller that sends it on every path. Two of them is a protocol violation, and
///     nothing in a password-configured test could ever reach it. Every branch of an authentication
///     handshake needs a client that actually takes it.
/// </remarks>
public sealed class PgWireAnonymousTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-pgwire-anon", Guid.NewGuid().ToString("N"));
    private ServiceProvider _services = null!;
    private IHostedService _server = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _port = FreePort();

        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<LakehouseOptions>(o =>
        {
            o.DataRoot = Path.Combine(_root, "data");
            o.BackupRoot = Path.Combine(_root, "backups");
            o.EjectRoot = Path.Combine(_root, "ejects");
        });
        services.Configure<PgWireOptions>(o =>
        {
            o.Enabled = true;
            o.Port = _port;
            o.AllowAnonymous = true;
        });

        services.AddDbContext<ControlPlaneContext>(o =>
            o.UseDuckDB($"Data Source={Path.Combine(_root, "controlplane.duckdb")}"));
        services.AddSingleton<DucklingPool>();
        services.AddSingleton<CatalogCache>();
        services.AddScoped<LakehouseService>();
        services.AddSingleton<IHostedService, PgWireServer>();

        _services = services.BuildServiceProvider();

        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ControlPlaneContext>().Database.EnsureCreatedAsync();
        }

        _server = _services.GetRequiredService<IHostedService>();
        await _server.StartAsync(default);
        await WaitForListenerAsync();
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync(default);
        await _services.DisposeAsync();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failing must not fail the test run.
        }
    }

    /// <summary>
    ///     Opening the connection is the assertion. A duplicated AuthenticationOk desynchronises the
    ///     client's read of the startup sequence, so a handshake that completes proves the message
    ///     was sent exactly once.
    /// </summary>
    [Fact]
    public async Task Anonymous_handshake_completes_without_a_password()
    {
        await using var connection = new NpgsqlConnection(
            $"Host=127.0.0.1;Port={_port};Database=anycatalog;Username=anytenant;"
            + "SSL Mode=Disable;Server Compatibility Mode=NoTypeLoading;Pooling=false;Timeout=30");

        await connection.OpenAsync();
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);

        // The catalog does not exist, so the first statement fails — but it fails on catalog
        // resolution rather than on the handshake, which is what this test is separating.
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());
        Assert.Equal("3D000", ex.SqlState);
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task WaitForListenerAsync()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, _port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(50);
            }
        }

        throw new InvalidOperationException($"The wire endpoint never bound to port {_port}.");
    }
}
