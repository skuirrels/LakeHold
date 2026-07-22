using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Lakehold.ControlPlane.Data;
using Lakehold.Engine.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Lakehold.Api.PgWire;

/// <summary>
///     One client connection, driven as a protocol state machine: startup, authentication, then a
///     message loop until the client terminates.
/// </summary>
/// <remarks>
///     <para>
///         A connection does not own a <see cref="Duckling"/>. Every statement resolves one through
///         <see cref="LakehouseService"/>, exactly as an HTTP request does, so idle eviction, the
///         session gate, and query history all apply unchanged. The cost is that no session state
///         survives between statements — see <c>docs/POSTGRES-WIRE.md</c>.
///     </para>
///     <para>
///         The protocol surface is deliberately narrow. Anything not implemented is refused with an
///         <c>ErrorResponse</c> rather than answered approximately: a client told "unsupported" can
///         fall back, whereas one handed a wrong encoding reports wrong numbers.
///     </para>
/// </remarks>
internal sealed class PgWireConnection(
    Stream stream,
    PgWireOptions options,
    IServiceScopeFactory scopeFactory,
    ILogger logger)
{
    private const int ProtocolVersion3 = 196608;
    private const int SslRequestCode = 80877103;
    private const int CancelRequestCode = 80877102;
    private const int GssEncRequestCode = 80877104;

    /// <summary>
    ///     The version reported to clients. Drivers gate features on it, and one that reads a
    ///     DuckDB version string either fails to parse it or assumes PostgreSQL 0.
    /// </summary>
    private const string ReportedServerVersion = "15.0";

    private readonly PgMessageWriter _writer = new();
    private readonly byte[] _header = new byte[5];

    private string _tenant = string.Empty;
    private string _catalog = string.Empty;

    // The extended-query protocol's unnamed statement and portal. Named ones are accepted and stored
    // in the same slots: a BI client uses one at a time, and pretending otherwise would mean tracking
    // lifetimes for state that no supported client actually relies on.
    private string _preparedSql = string.Empty;

    /// <summary>
    ///     Whether a <c>Describe</c> is awaiting the row shape that only executing can supply. See
    ///     the Describe case for why the reply is deferred rather than guessed.
    /// </summary>
    private bool _describePending;

    /// <summary>
    ///     Whether results are sent in binary format. Set per statement: the simple query protocol
    ///     is text-only by definition, while the extended protocol's clients need binary — see
    ///     <see cref="PgTypes.EncodeBinary"/> for why that is a requirement rather than a preference.
    /// </summary>
    private bool _binaryResults;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!await StartupAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await ReadExactAsync(_header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (!read)
            {
                return;
            }

            var tag = _header[0];
            if (tag == PgFrontend.Terminate)
            {
                return;
            }

            var body = await ReadBodyAsync(cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                return;
            }

            await DispatchAsync(tag, body, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(byte tag, byte[] body, CancellationToken cancellationToken)
    {
        switch (tag)
        {
            case PgFrontend.Query:
            {
                var reader = new PgMessageReader(body);
                var sql = reader.String();

                // The simple query protocol carries no format negotiation and is text by definition.
                _binaryResults = false;

                // One message may carry several statements, each answered with its own result set,
                // and only one ReadyForQuery at the end. Npgsql's type-catalogue load arrives this
                // way — four statements in one message — so treating the body as a single statement
                // desynchronises the client on its second expected result.
                var statements = PgStatementSplitter.Split(sql);

                if (statements.Count == 0)
                {
                    _writer.Reset();
                    _writer.Begin(PgBackend.EmptyQueryResponse).End();
                    await FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                foreach (var statement in statements)
                {
                    // A failed statement abandons the rest of the message, as Postgres does: the
                    // client resynchronises on ReadyForQuery and the remaining statements are not
                    // executed against a state the failure may have invalidated.
                    if (!await RunStatementAsync(statement, cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }
                }

                await SendReadyAsync(cancellationToken).ConfigureAwait(false);
                break;
            }

            case PgFrontend.Parse:
            {
                var reader = new PgMessageReader(body);
                _ = reader.String();            // statement name
                _preparedSql = reader.String();
                _ = reader.Int16();             // declared parameter types, ignored: see Bind
                _writer.Reset();
                _writer.Begin(PgBackend.ParseComplete).End();
                await FlushAsync(cancellationToken).ConfigureAwait(false);
                break;
            }

            case PgFrontend.Bind:
            {
                var reader = new PgMessageReader(body);
                _ = reader.String();            // portal name
                _ = reader.String();            // statement name
                var formatCount = reader.Int16();
                for (var i = 0; i < formatCount; i++)
                {
                    _ = reader.Int16();
                }

                var parameterCount = reader.Int16();
                if (parameterCount > 0)
                {
                    // Refused rather than substituted textually. Interpolating values into the SQL
                    // would be an injection surface inside the tenant's own session and would defeat
                    // the provider's own parameter handling; wiring that through is the first
                    // follow-up, and a client told "unsupported" can fall back to literal SQL.
                    await SendErrorAsync(
                        "0A000",
                        "Bound parameters are not supported by the Lakehold wire endpoint yet. Send literal SQL.",
                        cancellationToken).ConfigureAwait(false);
                    break;
                }

                // A single format code applies to every column; several apply one per column; none
                // means text. Npgsql asks for binary, and it must be given binary — it has no
                // text-format read path for int8, numeric, or timestamps.
                var resultFormatCount = reader.Int16();
                var binary = false;
                for (var i = 0; i < resultFormatCount; i++)
                {
                    // The first code decides for the whole row. A client that asked for a mix would
                    // be served uniformly rather than partially, which is the conservative reading:
                    // no client this endpoint targets sends a mixed request.
                    var code = reader.Int16();
                    binary |= i == 0 && code == 1;
                }

                _binaryResults = binary;

                _writer.Reset();
                _writer.Begin(PgBackend.BindComplete).End();
                await FlushAsync(cancellationToken).ConfigureAwait(false);
                break;
            }

            case PgFrontend.Describe:
            {
                var reader = new PgMessageReader(body);
                var kind = reader.Byte();

                if (kind == (byte)'S')
                {
                    // Statement: parameter types first. Always none, because Bind refuses them.
                    _writer.Reset();
                    _writer.Begin(PgBackend.ParameterDescription).Int16(0).End();
                    await FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                // The reply is deliberately deferred to Execute. The row shape is only knowable by
                // running the statement, and the alternatives are both worse: answering NoData tells
                // the client no rows are coming and it rejects the DataRows that follow, while
                // planning the statement a second time to learn its shape would execute every query
                // twice.
                //
                // Deferring costs nothing because the protocol's own ordering already requires the
                // Describe response to precede Execute's rows. The client sends
                // Parse/Bind/Describe/Execute/Sync as one batch and then reads, so emitting
                // RowDescription as the first thing Execute produces puts it exactly where a real
                // server would have put it.
                _describePending = true;
                break;
            }

            case PgFrontend.Execute:
            {
                await RunStatementAsync(_preparedSql, cancellationToken).ConfigureAwait(false);
                break;
            }

            case PgFrontend.Close:
            {
                _preparedSql = string.Empty;
                _writer.Reset();
                _writer.Begin(PgBackend.CloseComplete).End();
                await FlushAsync(cancellationToken).ConfigureAwait(false);
                break;
            }

            case PgFrontend.Sync:
                await SendReadyAsync(cancellationToken).ConfigureAwait(false);
                break;

            case PgFrontend.Flush:
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                break;

            default:
                await SendErrorAsync(
                    "08P01",
                    $"Unsupported protocol message '{(char)tag}'.",
                    cancellationToken).ConfigureAwait(false);
                await SendReadyAsync(cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Executes one statement, writing its result to the socket as rows arrive.</summary>
    /// <returns>True when the statement completed; false when an error was reported.</returns>
    private async Task<bool> RunStatementAsync(string sql, CancellationToken cancellationToken)
    {
        var trimmed = sql.Trim().TrimEnd(';').Trim();

        if (trimmed.Length == 0)
        {
            _writer.Reset();
            _writer.Begin(PgBackend.EmptyQueryResponse).End();
            await FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (PgCatalogShim.TryAnswer(trimmed, out var canned))
        {
            // A shimmed statement resolves an outstanding Describe too: its shape is known up front,
            // so the reply is whichever of RowDescription or NoData matches it.
            if (_describePending && canned.Columns.Count == 0)
            {
                _writer.Reset();
                _writer.Begin(PgBackend.NoData).End();
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            _describePending = false;
            await WriteCannedAsync(canned, cancellationToken).ConfigureAwait(false);
            return true;
        }

        try
        {
            // A scope per statement rather than per connection: LakehouseService is scoped because it
            // holds the control-plane DbContext, and a BI tool's connection can idle for hours.
            await using var scope = scopeFactory.CreateAsyncScope();
            var lakehouse = scope.ServiceProvider.GetRequiredService<LakehouseService>();

            var columnCount = 0;
            var rows = await lakehouse.StreamAsync(
                _tenant,
                _catalog,
                trimmed,
                async (columns, token) =>
                {
                    columnCount = columns.Count;

                    if (columns.Count > 0)
                    {
                        await WriteRowDescriptionAsync(columns, token).ConfigureAwait(false);
                    }
                    else if (_describePending)
                    {
                        // A statement with no result set. NoData is the correct Describe reply, and
                        // now it can be sent knowing it is true rather than assumed.
                        _writer.Reset();
                        _writer.Begin(PgBackend.NoData).End();
                        await FlushAsync(token).ConfigureAwait(false);
                    }

                    _describePending = false;
                },
                WriteDataRowAsync,
                options.MaxRows,
                cancellationToken).ConfigureAwait(false);

            await WriteCommandCompleteAsync(trimmed, columnCount, rows, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (CatalogNotFoundException ex)
        {
            // An ErrorResponse discharges any outstanding Describe: the client abandons the whole
            // extended-query sequence and resynchronises on Sync.
            _describePending = false;
            await SendErrorAsync("3D000", ex.Message, cancellationToken).ConfigureAwait(false);
        }
        catch (DuckDB.NET.Data.DuckDBException ex)
        {
            // Verbatim, as the HTTP path does: the engine's message names the offending token, which
            // is the only thing that makes a syntax error actionable in a BI tool's error box.
            _describePending = false;
            await SendErrorAsync("42601", ex.Message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _describePending = false;
            await SendErrorAsync("57014", "Statement cancelled or timed out.", cancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Type OIDs of the result being streamed, kept so DataRow encodes to match.</summary>
    private int[] _columnOids = [];

    private async Task WriteRowDescriptionAsync(IReadOnlyList<StreamColumn> columns, CancellationToken cancellationToken)
    {
        _columnOids = new int[columns.Count];
        var format = (short)(_binaryResults ? 1 : 0);

        _writer.Reset();
        _writer.Begin(PgBackend.RowDescription).Int16((short)columns.Count);

        for (var i = 0; i < columns.Count; i++)
        {
            var oid = PgTypes.OidFor(columns[i].ClrType);
            _columnOids[i] = oid;

            _writer.String(columns[i].Name)
                .Int32(0)               // table OID: not a real relation
                .Int16(0)               // column attribute number
                .Int32(oid)
                .Int16(-1)              // type size: variable
                .Int32(-1)              // type modifier
                .Int16(format);
        }

        _writer.End();
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Bytes to accumulate before writing to the socket while streaming rows.
    /// </summary>
    /// <remarks>
    ///     Flushing per row costs one write syscall per row — 10,000 of them for a table a BI tool
    ///     would call small, and the protocol has no need for that latency because the client is not
    ///     reading until the result ends. Batching keeps the streaming property that matters, which
    ///     is that no more than a bufferful is ever held, not that each row leaves individually.
    /// </remarks>
    private const int StreamFlushThreshold = 64 * 1024;

    private async Task WriteDataRowAsync(ReadOnlyMemory<object?> row, CancellationToken cancellationToken)
    {
        _writer.Begin(PgBackend.DataRow).Int16((short)row.Length);

        for (var i = 0; i < row.Length; i++)
        {
            var oid = i < _columnOids.Length ? _columnOids[i] : PgTypes.Text;
            var encoded = _binaryResults
                ? PgTypes.EncodeBinary(row.Span[i], oid)
                : PgTypes.Encode(row.Span[i]);

            _writer.Field(encoded, encoded is null);
        }

        _writer.End();

        if (_writer.Length >= StreamFlushThreshold)
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteCannedAsync(CannedResult canned, CancellationToken cancellationToken)
    {
        if (canned.Columns.Count > 0)
        {
            _writer.Reset();
            _writer.Begin(PgBackend.RowDescription).Int16((short)canned.Columns.Count);
            foreach (var name in canned.Columns)
            {
                _writer.String(name).Int32(0).Int16(0).Int32(PgTypes.Text).Int16(-1).Int32(-1).Int16(0);
            }

            _writer.End();

            foreach (var row in canned.Rows)
            {
                _writer.Begin(PgBackend.DataRow).Int16((short)row.Length);
                foreach (var value in row)
                {
                    var encoded = value is null ? null : Encoding.UTF8.GetBytes(value);
                    _writer.Field(encoded, encoded is null);
                }

                _writer.End();
            }

            _writer.Begin(PgBackend.CommandComplete)
                .String($"SELECT {canned.Rows.Count.ToString(CultureInfo.InvariantCulture)}")
                .End();
        }
        else
        {
            _writer.Reset();
            _writer.Begin(PgBackend.CommandComplete).String(canned.Tag).End();
        }

        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteCommandCompleteAsync(
        string sql,
        int columnCount,
        long rows,
        CancellationToken cancellationToken)
    {
        var count = rows.ToString(CultureInfo.InvariantCulture);
        var verb = FirstWord(sql);

        // A statement returning columns completes as SELECT n whatever its verb — DuckDB answers
        // DESCRIBE, SHOW, and CALL with result sets, and a client that asked for rows and is told
        // "CALL" will not read them.
        var tag = columnCount > 0
            ? $"SELECT {count}"
            : verb switch
            {
                "INSERT" => $"INSERT 0 {count}",
                "UPDATE" => $"UPDATE {count}",
                "DELETE" => $"DELETE {count}",
                "COPY" => $"COPY {count}",
                "" => "SELECT 0",
                _ => verb,
            };

        // Deliberately not Reset: rows buffered by WriteDataRowAsync since the last threshold flush
        // are still pending, and discarding them here would silently truncate every result whose
        // tail happened to fit under the buffer.
        _writer.Begin(PgBackend.CommandComplete).String(tag).End();
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string FirstWord(string sql)
    {
        var span = sql.AsSpan().TrimStart();
        var end = span.IndexOfAny(' ', '\t', '\n');
        var word = end < 0 ? span : span[..end];
        return word.ToString().ToUpperInvariant();
    }

    /// <summary>Reads the startup packet and authenticates, or closes the connection.</summary>
    private async Task<bool> StartupAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var lengthBuffer = new byte[4];
            if (!await ReadExactAsync(lengthBuffer, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lengthBuffer) - 4;
            if (length is < 0 or > 1_000_000)
            {
                return false;
            }

            var body = new byte[length];
            if (!await ReadExactAsync(body, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            var reader = new PgMessageReader(body);
            var code = reader.Int32();

            switch (code)
            {
                case SslRequestCode:
                case GssEncRequestCode:
                    // Declined, then the client re-sends a plain startup packet on the same socket.
                    await stream.WriteAsync(new byte[] { (byte)'N' }, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    continue;

                case CancelRequestCode:
                    // Accepted and ignored. Cancellation is per-statement through the session's own
                    // token; there is no cross-connection key registry to look the request up in.
                    return false;

                case ProtocolVersion3:
                    // Parsed here rather than inside the async method: PgMessageReader is a ref
                    // struct over the message body, and a ref parameter cannot cross an await.
                    return await AuthenticateAsync(ReadStartupParameters(ref reader), cancellationToken)
                        .ConfigureAwait(false);

                default:
                    await SendErrorAsync(
                        "0A000",
                        $"Unsupported protocol version {code.ToString(CultureInfo.InvariantCulture)}.",
                        cancellationToken).ConfigureAwait(false);
                    return false;
            }
        }
    }

    /// <summary>Reads the startup packet's null-terminated key/value pairs.</summary>
    private static Dictionary<string, string> ReadStartupParameters(ref PgMessageReader reader)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        while (!reader.IsAtEnd)
        {
            var key = reader.String();
            if (key.Length == 0)
            {
                break;
            }

            parameters[key] = reader.String();
        }

        return parameters;
    }

    private async Task<bool> AuthenticateAsync(
        Dictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        _tenant = parameters.GetValueOrDefault("user", string.Empty);
        _catalog = parameters.GetValueOrDefault("database", string.Empty);

        if (_tenant.Length == 0)
        {
            await SendErrorAsync("28000", "A user is required; it names the Lakehold tenant.", cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        // Postgres defaults the database to the user name. Lakehold has no such default — a tenant
        // may own several catalogs and guessing would attach the wrong one.
        if (_catalog.Length == 0 || _catalog == _tenant)
        {
            await SendErrorAsync(
                "3D000",
                "A database is required; it names the catalog to attach.",
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (!await CheckPasswordAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _writer.Reset();
        _writer.Begin(PgBackend.Authentication).Int32(0).End();

        foreach (var (key, value) in new[]
        {
            ("server_version", ReportedServerVersion),
            ("server_encoding", "UTF8"),
            ("client_encoding", "UTF8"),
            ("application_name", "lakehold"),
            ("DateStyle", "ISO, MDY"),
            ("TimeZone", "UTC"),
            ("integer_datetimes", "on"),
            ("standard_conforming_strings", "on"),
        })
        {
            _writer.Begin(PgBackend.ParameterStatus).String(key).String(value).End();
        }

        _writer.Begin(PgBackend.BackendKeyData).Int32(Environment.ProcessId).Int32(0).End();
        _writer.Begin(PgBackend.ReadyForQuery).Byte((byte)'I').End();
        await FlushAsync(cancellationToken).ConfigureAwait(false);

        PgWireLog.ConnectionOpened(logger, _tenant, _catalog);
        return true;
    }

    private async Task<bool> CheckPasswordAsync(CancellationToken cancellationToken)
    {
        if (options.Password.Length == 0)
        {
            // No exchange at all. AuthenticationOk is sent once by the caller for every path —
            // sending it here too would put two of them on the wire, which is a protocol violation
            // a strict client rejects. Start-up already refused to bind in this state unless
            // AllowAnonymous was set, so reaching here means the operator asked for it explicitly.
            return true;
        }

        var salt = RandomNumberGenerator.GetBytes(4);

        _writer.Reset();
        if (options.AllowCleartextPassword)
        {
            _writer.Begin(PgBackend.Authentication).Int32(3).End();
        }
        else
        {
            _writer.Begin(PgBackend.Authentication).Int32(5).Bytes(salt).End();
        }

        await FlushAsync(cancellationToken).ConfigureAwait(false);

        if (!await ReadExactAsync(_header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false)
            || _header[0] != PgFrontend.PasswordMessage)
        {
            return false;
        }

        var body = await ReadBodyAsync(cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return false;
        }

        var reader = new PgMessageReader(body);
        var presented = reader.String();

        var expected = options.AllowCleartextPassword
            ? options.Password
            : Md5Credential(options.Password, _tenant, salt);

        // Fixed-time comparison: the password is shared across every tenant, so leaking it through
        // response timing would leak all of them at once.
        var matched = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(expected));

        if (!matched)
        {
            PgWireLog.AuthenticationFailed(logger, _tenant);
            await SendErrorAsync("28P01", "Password authentication failed.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Computes PostgreSQL's MD5 credential: <c>md5(md5(password + user) + salt)</c>, hex-encoded.
    /// </summary>
    /// <remarks>
    ///     MD5 is not a choice here. The construction is fixed by the protocol's
    ///     <c>AuthenticationMD5Password</c> exchange, so computing anything else would simply fail to
    ///     authenticate any client. It is still an improvement on the alternative this endpoint
    ///     otherwise offers — <c>AllowCleartextPassword</c> — because the password does not cross an
    ///     unencrypted socket, and the per-connection salt makes the response useless to replay. The
    ///     real remedy is TLS and SCRAM-SHA-256, both of which are follow-ups rather than
    ///     substitutions available at this call site.
    /// </remarks>
#pragma warning disable CA5351 // Protocol-mandated: see remarks.
    internal static string Md5Credential(string password, string user, ReadOnlySpan<byte> salt)
    {
        var inner = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(password + user)))
            .ToLowerInvariant();

        var outerInput = new byte[Encoding.UTF8.GetByteCount(inner) + salt.Length];
        var written = Encoding.UTF8.GetBytes(inner, outerInput);
        salt.CopyTo(outerInput.AsSpan(written));

        return "md5" + Convert.ToHexString(MD5.HashData(outerInput)).ToLowerInvariant();
    }
#pragma warning restore CA5351

    private async Task SendErrorAsync(string code, string message, CancellationToken cancellationToken)
    {
        _writer.Reset();
        _writer.Begin(PgBackend.ErrorResponse)
            .Byte((byte)'S').String("ERROR")
            .Byte((byte)'C').String(code)
            .Byte((byte)'M').String(message)
            .Byte(0)
            .End();

        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SendReadyAsync(CancellationToken cancellationToken)
    {
        _writer.Reset();
        _writer.Begin(PgBackend.ReadyForQuery).Byte((byte)'I').End();
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        await stream.WriteAsync(_writer.Written, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        _writer.Reset();
    }

    private async Task<byte[]?> ReadBodyAsync(CancellationToken cancellationToken)
    {
        if (!await ReadExactAsync(_header.AsMemory(1, 4), cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(1, 4)) - 4;
        if (length is < 0 or > 100_000_000)
        {
            return null;
        }

        var body = new byte[length];
        return await ReadExactAsync(body, cancellationToken).ConfigureAwait(false) ? body : null;
    }

    private async Task<bool> ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
