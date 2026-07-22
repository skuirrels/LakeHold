using System.Net;
using System.Net.Sockets;
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
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Drives the wire endpoint with a real client.
/// </summary>
/// <remarks>
///     <para>
///         Npgsql is the point of this fixture. Power BI's PostgreSQL connector is built on it, so a
///         test that passes here has satisfied the same startup handshake, authentication exchange,
///         and extended-query sequence a BI tool performs — none of which a hand-written client
///         would reproduce faithfully, because it would send what we expected rather than what a
///         driver actually sends.
///     </para>
///     <para>
///         The whole stack is real: a DuckLake catalog on disk, the control plane resolving the
///         tenant, and the session pool serving the statement. Mocking any of it would leave the one
///         claim worth testing — that a BI client can read a Lakehold catalog — unverified.
///     </para>
/// </remarks>
public sealed class PgWireEndpointTests : IAsyncLifetime
{
    private const string Tenant = "wire";
    private const string Catalog = "wirelake";
    private const string Password = "wire-endpoint-secret";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-pgwire", Guid.NewGuid().ToString("N"));
    private ServiceProvider _services = null!;
    private IHostedService _server = null!;
    private int _port;

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
            o.Password = Password;

            // Short enough to assert against, long enough that a loaded CI machine still completes
            // a legitimate handshake inside it.
            o.HandshakeTimeout = TimeSpan.FromSeconds(3);
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

            var tenant = new Tenant { Slug = Tenant, DisplayName = "Wire tests" };
            tenant.Catalogs.Add(new LakeCatalog
            {
                Name = Catalog,
                MetadataKind = CatalogMetadataKind.LocalFile,
                MetadataSource = Path.Combine(_root, $"{Catalog}.ducklake"),
                DataPath = dataPath,
            });

            context.Tenants.Add(tenant);
            await context.SaveChangesAsync();

            var lakehouse = scope.ServiceProvider.GetRequiredService<LakehouseService>();
            await lakehouse.ExecuteAsync(Tenant, Catalog, "CREATE TABLE events (id BIGINT, name VARCHAR, amount DOUBLE, ok BOOLEAN)", default);
            await lakehouse.ExecuteAsync(
                Tenant,
                Catalog,
                "INSERT INTO events VALUES (1, 'alpha', 1.5, true), (2, 'beta', 2.5, false), (3, NULL, NULL, NULL)",
                default);
        }

        _server = _services.GetRequiredService<IHostedService>();
        await _server.StartAsync(default);

        // The listener binds inside the hosted service's ExecuteAsync, so give it a moment before
        // the first connect rather than racing it.
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

    private string ConnectionString =>
        $"Host=127.0.0.1;Port={_port};Database={Catalog};Username={Tenant};Password={Password};"
        + "SSL Mode=Disable;Server Compatibility Mode=NoTypeLoading;Pooling=false;Timeout=30";

    [Fact]
    public async Task Npgsql_connects_and_reads_rows()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("SELECT id, name, amount, ok FROM events ORDER BY id", connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("alpha", reader.GetString(1));
        Assert.Equal(1.5, reader.GetDouble(2));
        Assert.True(reader.GetBoolean(3));

        Assert.True(await reader.ReadAsync());
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.False(reader.GetBoolean(3));

        // NULL must arrive as NULL rather than as an empty string, which is the failure mode of
        // writing a zero-length field instead of the -1 sentinel.
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.IsDBNull(2));

        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task Column_types_are_declared_so_the_client_reads_them_natively()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("SELECT id, name, amount, ok FROM events LIMIT 1", connection);
        await using var reader = await command.ExecuteReaderAsync();

        // If the OIDs were wrong the client would still receive the bytes but hand back the wrong
        // CLR types, which is exactly how a BI tool ends up charting text.
        Assert.Equal(typeof(long), reader.GetFieldType(0));
        Assert.Equal(typeof(string), reader.GetFieldType(1));
        Assert.Equal(typeof(double), reader.GetFieldType(2));
        Assert.Equal(typeof(bool), reader.GetFieldType(3));
    }

    [Fact]
    public async Task Aggregates_and_scalars_round_trip()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("SELECT count(*) FROM events", connection);
        Assert.Equal(3L, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    ///     The row ceiling that bounds the JSON path must not bound this one: a BI tool given a
    ///     silent prefix of a table reports a confidently wrong number.
    /// </summary>
    [Fact]
    public async Task Results_stream_past_the_http_row_ceiling()
    {
        var ceiling = _services.GetRequiredService<IOptions<LakehouseOptions>>().Value.MaxRowsPerResult;
        Assert.Equal(10_000, ceiling);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            $"SELECT i FROM range(0, {ceiling + 500}) t(i)", connection);
        await using var reader = await command.ExecuteReaderAsync();

        var rows = 0;
        while (await reader.ReadAsync())
        {
            rows++;
        }

        Assert.Equal(ceiling + 500, rows);
    }

    /// <summary>
    ///     The binary encoders are hand-written and their failures are silent: a wrong base-10000
    ///     weight renders 1.5 as 15000 rather than erroring, and a timestamp off by the epoch lands
    ///     in 1970 or 2030 while still parsing cleanly. Only a real client reading them back catches
    ///     that, which is why these assert on values rather than on bytes.
    /// </summary>
    [Fact]
    public async Task Decimals_and_timestamps_survive_the_binary_encoding()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            SELECT CAST(1.5 AS DECIMAL(18,2))            AS small,
                   CAST(-12345.6789 AS DECIMAL(18,4))    AS negative,
                   CAST(0 AS DECIMAL(18,2))              AS zero,
                   CAST(99999999.99 AS DECIMAL(18,2))    AS large,
                   TIMESTAMP '2026-07-22 13:45:06.123456' AS ts,
                   DATE '2026-07-22'                      AS d
            """,
            connection);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal(1.5m, reader.GetDecimal(0));
        Assert.Equal(-12345.6789m, reader.GetDecimal(1));
        Assert.Equal(0m, reader.GetDecimal(2));
        Assert.Equal(99999999.99m, reader.GetDecimal(3));
        Assert.Equal(new DateTime(2026, 7, 22, 13, 45, 6, DateTimeKind.Unspecified).AddTicks(1234560), reader.GetDateTime(4));
        Assert.Equal(new DateTime(2026, 7, 22), reader.GetDateTime(5));
    }

    /// <summary>
    ///     A peer that connects and says nothing must be dropped rather than holding its slot.
    /// </summary>
    /// <remarks>
    ///     Untested, this is a denial of service costing one TCP handshake per slot: MaxConnections
    ///     silent sockets and no legitimate client can connect again until the process restarts.
    ///     The fixture's handshake timeout is shortened so the test asserts the behaviour rather
    ///     than waiting out the production default.
    /// </remarks>
    [Fact]
    public async Task Silent_client_is_dropped_at_the_handshake_timeout()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        await using var stream = client.GetStream();

        // Never send a startup packet. The server should close rather than wait indefinitely.
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(0, read);
    }

    /// <summary>
    ///     The length prefix sizes an allocation and is attacker-controlled, so an unauthenticated
    ///     peer must not be able to name a large one.
    /// </summary>
    [Fact]
    public async Task Oversized_pre_authentication_message_is_refused()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        await using var stream = client.GetStream();

        await SendStartupAsync(stream);

        // Read the authentication request, then answer with a password message claiming 64 MB.
        var header = new byte[13];
        await stream.ReadExactlyAsync(header);

        var oversized = new byte[5];
        oversized[0] = (byte)'p';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(oversized.AsSpan(1, 4), 64 * 1024 * 1024);
        await stream.WriteAsync(oversized);
        await stream.FlushAsync();

        // The server must close instead of allocating what the header asked for.
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(0, read);
    }

    [Fact]
    public async Task Wrong_password_is_refused()
    {
        await using var connection = new NpgsqlConnection(
            ConnectionString.Replace(Password, "not-the-password", StringComparison.Ordinal));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => connection.OpenAsync());
        Assert.Contains("authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_catalog_is_refused_at_the_first_statement()
    {
        await using var connection = new NpgsqlConnection(
            ConnectionString.Replace($"Database={Catalog}", "Database=nosuchcatalog", StringComparison.Ordinal));
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("SELECT 1", connection);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());

        // 3D000 is invalid_catalog_name, which is what a client expects to see for this.
        Assert.Equal("3D000", ex.SqlState);
    }

    [Fact]
    public async Task Engine_errors_reach_the_client_verbatim()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("SELECT * FROM no_such_table", connection);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());

        Assert.Contains("no_such_table", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Every statement lands in query history, including the ones a BI tool sends on its own
    ///     initiative. That visibility is the reason the endpoint enters through LakehouseService
    ///     rather than reaching into the pool directly.
    /// </summary>
    [Fact]
    public async Task Statements_are_recorded_in_query_history()
    {
        const string Marker = "SELECT 424242";

        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(Marker, connection);
            await command.ExecuteScalarAsync();
        }

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
        var recorded = await context.QueryRuns.AsNoTracking().Where(r => r.Sql == Marker).ToListAsync();

        Assert.Single(recorded);
        Assert.True(recorded[0].Succeeded);
    }

    /// <summary>
    ///     Drives the simple query protocol over a raw socket, which no Npgsql test reaches: Npgsql
    ///     always uses the extended protocol, so the text-format path that <c>psql</c> and older
    ///     tools take would otherwise ship unexercised.
    /// </summary>
    [Fact]
    public async Task Simple_query_protocol_returns_text_format()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        await using var stream = client.GetStream();

        await SendStartupAsync(stream);
        await SendPasswordAsync(stream);

        // Query message: 'Q', length, null-terminated SQL.
        await SendAsync(stream, (byte)'Q', w => w.String("SELECT name FROM events WHERE id = 1"));

        var (rowDescription, dataRow) = await ReadUntilRowAsync(stream);

        // Format code is the last int16 of the field description: 0 is text, and the simple query
        // protocol has no way to ask for anything else.
        Assert.Equal(0, rowDescription);

        // 'alpha' as plain UTF-8 rather than a binary-encoded value.
        Assert.Equal("alpha", dataRow);
    }

    private static async Task SendStartupAsync(Stream stream)
    {
        var writer = new PgMessageWriter();
        writer.Int32(0).Int32(196608).String("user").String(Tenant).String("database").String(Catalog).Byte(0);

        var bytes = writer.Written.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0, 4), bytes.Length);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static async Task SendPasswordAsync(Stream stream)
    {
        // AuthenticationMD5Password: 'R', length 12, int32 5, four salt bytes.
        var header = new byte[13];
        await stream.ReadExactlyAsync(header);
        Assert.Equal((byte)'R', header[0]);
        Assert.Equal(5, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(5, 4)));

        var credential = PgWireConnection.Md5Credential(Password, Tenant, header.AsSpan(9, 4));
        await SendAsync(stream, (byte)'p', w => w.String(credential));

        // Drain AuthenticationOk, ParameterStatus messages, BackendKeyData, and ReadyForQuery.
        await ReadUntilAsync(stream, (byte)'Z');
    }

    private static async Task SendAsync(Stream stream, byte tag, Action<PgMessageWriter> build)
    {
        var writer = new PgMessageWriter();
        writer.Begin(tag);
        build(writer);
        writer.End();
        await stream.WriteAsync(writer.Written);
        await stream.FlushAsync();
    }

    private static async Task<byte[]> ReadUntilAsync(Stream stream, byte tag)
    {
        while (true)
        {
            var header = new byte[5];
            await stream.ReadExactlyAsync(header);
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1, 4)) - 4;
            var body = new byte[length];
            await stream.ReadExactlyAsync(body);

            if (header[0] == tag)
            {
                return body;
            }
        }
    }

    private static async Task<(short Format, string Value)> ReadUntilRowAsync(Stream stream)
    {
        var description = await ReadUntilAsync(stream, (byte)'T');
        var format = System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(description.AsSpan(^2..));

        var row = await ReadUntilAsync(stream, (byte)'D');
        var valueLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(row.AsSpan(2, 4));
        var value = System.Text.Encoding.UTF8.GetString(row.AsSpan(6, valueLength));

        return (format, value);
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
