using System.Buffers.Binary;
using System.Text;
using Lakehold.Api.PgWire;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Cover for the wire codec and its type projection.
/// </summary>
/// <remarks>
///     These are the parts a live client exercises but cannot diagnose: a length field off by four
///     desynchronises the stream and every later message is read at the wrong offset, which surfaces
///     as an unrelated parse failure several messages downstream. Asserting on the bytes is the only
///     place that failure is legible.
/// </remarks>
public sealed class PgProtocolTests
{
    [Fact]
    public void Message_length_counts_itself_but_not_the_tag()
    {
        var writer = new PgMessageWriter();
        writer.Begin((byte)'Z').Byte((byte)'I').End();

        var bytes = writer.Written.Span;

        Assert.Equal((byte)'Z', bytes[0]);

        // 4 length bytes + 1 payload byte. The tag is excluded by definition, and getting this
        // wrong is the single most destructive framing bug available.
        Assert.Equal(5, BinaryPrimitives.ReadInt32BigEndian(bytes[1..5]));
        Assert.Equal(6, bytes.Length);
    }

    [Fact]
    public void Strings_are_null_terminated_utf8()
    {
        var writer = new PgMessageWriter();
        writer.Begin((byte)'C').String("SELECT 3").End();

        var body = writer.Written.Span[5..];
        Assert.Equal("SELECT 3"u8.ToArray(), body[..^1].ToArray());
        Assert.Equal(0, body[^1]);
    }

    [Fact]
    public void Reader_round_trips_what_the_writer_produced()
    {
        var writer = new PgMessageWriter();
        writer.Begin((byte)'P').String("stmt").String("SELECT 1").Int16(0).End();

        var reader = new PgMessageReader(writer.Written.Span[5..]);

        Assert.Equal("stmt", reader.String());
        Assert.Equal("SELECT 1", reader.String());
        Assert.Equal(0, reader.Int16());
        Assert.True(reader.IsAtEnd);
    }

    [Fact]
    public void Null_fields_use_the_minus_one_sentinel()
    {
        var writer = new PgMessageWriter();
        writer.Begin((byte)'D').Int16(1).Field([], isNull: true).End();

        var body = writer.Written.Span[5..];
        Assert.Equal(-1, BinaryPrimitives.ReadInt32BigEndian(body[2..6]));
    }

    [Fact]
    public void Growing_past_the_initial_buffer_preserves_earlier_bytes()
    {
        var writer = new PgMessageWriter();
        var large = new string('x', 9000);

        writer.Begin((byte)'C').String(large).End();

        var bytes = writer.Written.Span;
        Assert.Equal((byte)'C', bytes[0]);
        Assert.Equal(9000 + 1 + 4, BinaryPrimitives.ReadInt32BigEndian(bytes[1..5]));
        Assert.Equal(large, Encoding.UTF8.GetString(bytes[5..^1]));
    }

    /// <summary>
    ///     The MD5 credential is the one value a client computes independently, so a wrong
    ///     construction fails every login with no clue as to which half is wrong.
    /// </summary>
    [Fact]
#pragma warning disable CA5351 // The construction under test is fixed by the protocol.
    public void Md5_credential_matches_the_postgres_construction()
    {
        var salt = new byte[] { 1, 2, 3, 4 };

        var actual = PgWireConnection.Md5Credential("secret", "demo", salt);

        // md5(md5("secret" + "demo") + salt), independently computed.
        var inner = Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("secretdemo")))
            .ToLowerInvariant();
        var outerInput = Encoding.UTF8.GetBytes(inner).Concat(salt).ToArray();
        var expected = "md5" + Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(outerInput)).ToLowerInvariant();

        Assert.Equal(expected, actual);
        Assert.StartsWith("md5", actual, StringComparison.Ordinal);
        Assert.Equal(35, actual.Length);
    }
#pragma warning restore CA5351

    [Theory]
    [InlineData(true, "t")]
    [InlineData(false, "f")]
    public void Booleans_use_postgres_text_spelling(bool value, string expected)
    {
        // "true"/"false" is accepted on input but is not what a client parsing a bool column reads.
        Assert.Equal(expected, Text(PgTypes.Encode(value)));
    }

    [Fact]
    public void Timestamps_use_a_space_separator_rather_than_iso_t()
    {
        var value = new DateTime(2026, 7, 22, 13, 45, 6, DateTimeKind.Unspecified);
        Assert.Equal("2026-07-22 13:45:06.000000", Text(PgTypes.Encode(value)));
    }

    [Fact]
    public void Byte_arrays_use_hex_bytea_format()
    {
        Assert.Equal("\\xdeadbeef", Text(PgTypes.Encode(new byte[] { 0xde, 0xad, 0xbe, 0xef })));
    }

    [Fact]
    public void Null_encodes_to_null_rather_than_an_empty_string()
    {
        // An empty byte array is a zero-length value, which is a different thing from SQL NULL.
        Assert.Null(PgTypes.Encode(null));
        Assert.Equal(string.Empty, Text(PgTypes.Encode(string.Empty)));
    }

    [Fact]
    public void Wide_integers_stay_exact_and_are_declared_as_text()
    {
        // ulong and BigInteger exceed int8, so declaring them int8 would have the client parse a
        // value it cannot hold. Text keeps the digits intact.
        Assert.Equal(PgTypes.Text, PgTypes.OidFor(typeof(ulong)));
        Assert.Equal("18446744073709551615", Text(PgTypes.Encode(ulong.MaxValue)));
    }

    [Theory]
    [InlineData(typeof(bool), PgTypes.Bool)]
    [InlineData(typeof(int), PgTypes.Int4)]
    [InlineData(typeof(long), PgTypes.Int8)]
    [InlineData(typeof(double), PgTypes.Float8)]
    [InlineData(typeof(decimal), PgTypes.Numeric)]
    [InlineData(typeof(string), PgTypes.Varchar)]
    [InlineData(typeof(DateTime), PgTypes.Timestamp)]
    [InlineData(typeof(Guid), PgTypes.Uuid)]
    [InlineData(typeof(byte[]), PgTypes.Bytea)]
    public void Clr_types_map_to_the_expected_oids(Type clrType, int expected)
        => Assert.Equal(expected, PgTypes.OidFor(clrType));

    [Fact]
    public void Nullable_types_map_as_their_underlying_type()
        => Assert.Equal(PgTypes.Int4, PgTypes.OidFor(typeof(int?)));

    private static string? Text(byte[]? encoded) => encoded is null ? null : Encoding.UTF8.GetString(encoded);
}
