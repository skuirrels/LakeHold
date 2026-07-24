using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api.PgWire;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
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
///     Cover for the wire endpoint on the shared token store: an API token authenticates a BI client,
///     revoking it closes that surface as it closes the HTTP one, and a token cannot be used to reach a
///     tenant or catalog it does not name. Exercised through Npgsql over a real socket, because the
///     point is that a real client's exchange works, not that our own helper agrees with itself.
/// </summary>
public sealed class PgWireTokenAuthTests : IAsyncLifetime
{
    private const string TenantA = "alpha";
    private const string TenantB = "beta";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-pgwire-token", Guid.NewGuid().ToString("N"));
    private ServiceProvider _services = null!;
    private IHostedService _server = null!;
    private int _port;

    private string _tokenA = null!;
    private string _readOnlyTokenA = null!;
    private string _narrowedTokenA = null!;
    private string _revokedTokenA = null!;
    private int _revokedId;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var dataPath = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataPath);
        _port = FreePort();

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
            o.AllowTokenAuthentication = true;

            // The test drives plaintext loopback deliberately; production refuses this pairing at
            // start-up unless TLS is required (see Program.cs).
            o.AllowCleartextPassword = true;
            o.TlsCertificatePath = WriteSelfSignedCertificate();
        });

        services.AddDbContext<ControlPlaneContext>(o =>
            o.UseDuckDB($"Data Source={Path.Combine(_root, "controlplane.duckdb")}"));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ApiTokenAuthenticator>();
        services.AddSingleton<DucklingPool>();
        services.AddSingleton<CatalogCache>();
        services.AddScoped<LakehouseService>();
        services.AddSingleton<IHostedService, PgWireServer>();

        _services = services.BuildServiceProvider();

        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
            await context.Database.EnsureCreatedAsync();

            Tenant? alpha = null;
            foreach (var slug in new[] { TenantA, TenantB })
            {
                var tenant = new Tenant { Slug = slug, DisplayName = slug, CreatedUtc = DateTimeOffset.UtcNow };
                tenant.Catalogs.Add(new LakeCatalog
                {
                    Name = slug + "lake",
                    MetadataKind = CatalogMetadataKind.LocalFile,
                    MetadataSource = Path.Combine(_root, $"{slug}.ducklake"),
                    DataPath = Path.Combine(dataPath, slug),
                    CreatedUtc = DateTimeOffset.UtcNow,
                });

                Directory.CreateDirectory(Path.Combine(dataPath, slug));
                context.Tenants.Add(tenant);
                if (slug == TenantA)
                {
                    alpha = tenant;
                }
            }

            await context.SaveChangesAsync();

            var now = DateTimeOffset.UtcNow;
            _tokenA = Persist(context, ApiTokenFactory.Issue(TokenScope.Tenant, alpha!, "bi", now));
            _readOnlyTokenA = Persist(context, ApiTokenFactory.Issue(TokenScope.Tenant, alpha!, "ro", now, readOnly: true));
            _narrowedTokenA = Persist(
                context, ApiTokenFactory.Issue(TokenScope.Tenant, alpha!, "narrowed", now, catalogName: "somethingelse"));

            var revoked = ApiTokenFactory.Issue(TokenScope.Tenant, alpha!, "revoked", now);
            _revokedTokenA = revoked.Plaintext;
            context.ApiTokens.Add(revoked.Record);

            await context.SaveChangesAsync();
            _revokedId = revoked.Record.Id;
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

    [Fact]
    public async Task An_api_token_authenticates_a_wire_client()
    {
        await using var connection = new NpgsqlConnection(Connection(TenantA, _tokenA));
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("SELECT 1", connection);
        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_revoked_token_is_refused_on_the_wire()
    {
        // It works first, so the refusal that follows is attributable to the revocation alone.
        await using (var before = new NpgsqlConnection(Connection(TenantA, _revokedTokenA)))
        {
            await before.OpenAsync();
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
            var token = await context.ApiTokens.SingleAsync(t => t.Id == _revokedId);
            token.RevokedUtc = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }

        await using var after = new NpgsqlConnection(Connection(TenantA, _revokedTokenA));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => after.OpenAsync());
        Assert.Contains("authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_tokens_tenant_must_match_the_connection()
    {
        await using var connection = new NpgsqlConnection(Connection(TenantB, _tokenA));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => connection.OpenAsync());
        Assert.Contains("authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_catalog_narrowed_token_cannot_open_another_catalog()
    {
        // The token names catalog 'somethingelse'; the connection asks for 'alphalake'.
        await using var connection = new NpgsqlConnection(Connection(TenantA, _narrowedTokenA));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => connection.OpenAsync());
        Assert.Contains("authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_read_only_token_is_refused_a_write_by_the_engine()
    {
        // Initialise the catalog through the read-write token first. A read-only attachment cannot
        // create a DuckLake metadata file, so a catalog that was never written has nothing to open —
        // the same reason a read-only replica needs the database to exist before it can serve it.
        await using (var writer = new NpgsqlConnection(Connection(TenantA, _tokenA)))
        {
            await writer.OpenAsync();
            await using var seed = new NpgsqlCommand("CREATE TABLE seeded (id BIGINT)", writer);
            await seed.ExecuteNonQueryAsync();
        }

        await using var connection = new NpgsqlConnection(Connection(TenantA, _readOnlyTokenA));
        await connection.OpenAsync();

        // Reads work through the read-only session.
        await using (var read = new NpgsqlCommand("SELECT count(*) FROM seeded", connection))
        {
            Assert.Equal(0, Convert.ToInt32(await read.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        }

        // The write is refused by the engine, not by inspecting the statement.
        await using var write = new NpgsqlCommand("INSERT INTO seeded VALUES (1)", connection);
        await Assert.ThrowsAnyAsync<Exception>(() => write.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task A_garbage_password_is_refused()
    {
        await using var connection = new NpgsqlConnection(Connection(TenantA, "lkh_alpha_not-the-real-secret"));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => connection.OpenAsync());
        Assert.Contains("authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string Persist(ControlPlaneContext context, IssuedToken issued)
    {
        context.ApiTokens.Add(issued.Record);
        return issued.Plaintext;
    }

    private string Connection(string tenant, string password) =>
        $"Host=localhost;Port={_port};Database={tenant}lake;Username={tenant};Password={password};"
        + "SSL Mode=Disable;Trust Server Certificate=true;"
        + "Server Compatibility Mode=NoTypeLoading;Pooling=false;Timeout=30";

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
