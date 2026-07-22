using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api.PgWire;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Cover for the two properties that decide whether this endpoint may face a network: that the
///     session can be encrypted, and that a credential unlocks one tenant rather than all of them.
/// </summary>
/// <remarks>
///     Both are asserted with a real client over a real socket. A shared password and per-tenant
///     passwords produce identical successful connections for the tenant that owns the credential —
///     the difference only shows up in what a <em>second</em> tenant can do with it, which is why the
///     cross-tenant refusal is the test that matters here rather than the happy path.
/// </remarks>
public sealed class PgWireSecurityTests : IAsyncLifetime
{
    private const string TenantA = "alpha";
    private const string TenantB = "beta";
    private const string PasswordA = "alpha-secret-password";
    private const string PasswordB = "beta-secret-password";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-pgwire-sec", Guid.NewGuid().ToString("N"));
    private ServiceProvider _services = null!;
    private IHostedService _server = null!;
    private int _port;
    private string _certPath = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var dataPath = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataPath);
        _port = FreePort();
        _certPath = WriteSelfSignedCertificate();

        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<LakehouseOptions>(o =>
        {
            o.DataRoot = dataPath;
            o.BackupRoot = Path.Combine(_root, "backups");
            o.EjectRoot = Path.Combine(_root, "ejects");
        });
        services.Configure<PgWireOptions>(o =>
        {
            o.Enabled = true;
            o.Port = _port;
            o.TlsCertificatePath = _certPath;
            o.TenantPasswords[TenantA] = PasswordA;
            o.TenantPasswords[TenantB] = PasswordB;
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
            var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
            await context.Database.EnsureCreatedAsync();

            foreach (var slug in new[] { TenantA, TenantB })
            {
                var tenant = new Tenant { Slug = slug, DisplayName = slug };
                tenant.Catalogs.Add(new LakeCatalog
                {
                    Name = slug + "lake",
                    MetadataKind = CatalogMetadataKind.LocalFile,
                    MetadataSource = Path.Combine(_root, $"{slug}.ducklake"),
                    DataPath = Path.Combine(dataPath, slug),
                });

                Directory.CreateDirectory(Path.Combine(dataPath, slug));
                context.Tenants.Add(tenant);
            }

            await context.SaveChangesAsync();
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

    private string Connection(string tenant, string password, string sslMode) =>
        $"Host=localhost;Port={_port};Database={tenant}lake;Username={tenant};Password={password};"
        + $"SSL Mode={sslMode};Trust Server Certificate=true;"
        + "Server Compatibility Mode=NoTypeLoading;Pooling=false;Timeout=30";

    [Fact]
    public async Task Session_is_encrypted_when_the_client_requests_tls()
    {
        await using var connection = new NpgsqlConnection(Connection(TenantA, PasswordA, "Require"));
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("SELECT 1", connection);
        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Plaintext_still_works_when_tls_is_offered_but_not_required()
    {
        await using var connection = new NpgsqlConnection(Connection(TenantA, PasswordA, "Disable"));
        await connection.OpenAsync();

        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    /// <summary>
    ///     The point of per-tenant credentials: one tenant's password must not open another's
    ///     catalog. Under the shared-password model this connection succeeded.
    /// </summary>
    [Fact]
    public async Task One_tenants_password_does_not_authenticate_another()
    {
        await using var connection = new NpgsqlConnection(Connection(TenantB, PasswordA, "Disable"));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => connection.OpenAsync());
        Assert.Contains("authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Each_tenant_authenticates_with_its_own_password()
    {
        foreach (var (tenant, password) in new[] { (TenantA, PasswordA), (TenantB, PasswordB) })
        {
            await using var connection = new NpgsqlConnection(Connection(tenant, password, "Disable"));
            await connection.OpenAsync();
            Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        }
    }

    /// <summary>
    ///     An unconfigured tenant is refused, and is refused the same way a wrong password is, so the
    ///     response does not disclose which tenants have credentials.
    /// </summary>
    [Fact]
    public async Task Tenant_with_no_configured_password_is_refused()
    {
        await using var connection = new NpgsqlConnection(Connection(TenantA, PasswordA, "Disable")
            .Replace($"Username={TenantA}", "Username=nosuchtenant", StringComparison.Ordinal));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => connection.OpenAsync());
        Assert.Contains("authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private string WriteSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var path = Path.Combine(_root, "wire.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
        return path;
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
