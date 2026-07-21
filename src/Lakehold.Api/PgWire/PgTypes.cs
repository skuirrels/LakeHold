using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Lakehold.Api.PgWire;

/// <summary>
///     Maps the CLR values the provider materialises onto PostgreSQL type OIDs and their text
///     representations.
/// </summary>
/// <remarks>
///     <para>
///         This is the wire projection for the Postgres endpoint, and the counterpart to
///         <c>Duckling.ToWireValue</c>, which does the same job for JSON. They are deliberately
///         separate: JSON's constraint is that integers past 2^53 lose precision in a browser, while
///         this format's constraint is that a client parses each value according to the OID we
///         declared for its column. Sharing one projection would mean serving one of them wrongly.
///     </para>
///     <para>
///         Every value is sent in text format. A client asking for binary is refused rather than
///         answered in the wrong encoding — see <c>docs/POSTGRES-WIRE.md</c>.
///     </para>
/// </remarks>
internal static class PgTypes
{
    // Type OIDs, from the PostgreSQL catalog. These are stable across versions by definition:
    // clients hard-code them, so they can never be renumbered.
    public const int Bool = 16;
    public const int Bytea = 17;
    public const int Int8 = 20;
    public const int Int2 = 21;
    public const int Int4 = 23;
    public const int Text = 25;
    public const int Float4 = 700;
    public const int Float8 = 701;
    public const int Varchar = 1043;
    public const int Date = 1082;
    public const int Time = 1083;
    public const int Timestamp = 1114;
    public const int Timestamptz = 1184;
    public const int Interval = 1186;
    public const int Numeric = 1700;
    public const int Uuid = 2950;

    /// <summary>Chooses the OID a column should be described with.</summary>
    /// <remarks>
    ///     Driven by the CLR type the provider reports rather than DuckDB's type name, because that
    ///     is what the values actually arrive as — a DuckDB <c>HUGEINT</c> and a <c>DECIMAL</c> both
    ///     surface as CLR types this can dispatch on, whereas matching type-name strings would need
    ///     to keep pace with DuckDB's own naming.
    /// </remarks>
    public static int OidFor(Type? clrType)
    {
        if (clrType is null)
        {
            return Text;
        }

        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;

        return Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => Bool,
            TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 => Int2,
            TypeCode.UInt16 or TypeCode.Int32 => Int4,
            TypeCode.UInt32 or TypeCode.Int64 => Int8,
            TypeCode.Single => Float4,
            TypeCode.Double => Float8,
            TypeCode.Decimal => Numeric,
            TypeCode.DateTime => Timestamp,
            TypeCode.String or TypeCode.Char => Varchar,
            _ => OidForNonPrimitive(type),
        };
    }

    private static int OidForNonPrimitive(Type type)
    {
        if (type == typeof(Guid))
        {
            return Uuid;
        }

        if (type == typeof(byte[]))
        {
            return Bytea;
        }

        if (type == typeof(DateTimeOffset))
        {
            return Timestamptz;
        }

        if (type == typeof(DateOnly))
        {
            return Date;
        }

        if (type == typeof(TimeOnly) || type == typeof(TimeSpan))
        {
            return type == typeof(TimeOnly) ? Time : Interval;
        }

        // UInt64 and BigInteger exceed int8's range, and LIST/STRUCT/MAP have no scalar equivalent.
        // Text is the honest answer for all of them: the value survives intact and the client is not
        // told to parse it as something it is not.
        return Text;
    }

    /// <summary>
    ///     Renders a value in PostgreSQL's text format, or returns null for SQL NULL.
    /// </summary>
    public static byte[]? Encode(object? value) => value switch
    {
        null or DBNull => null,

        // Postgres spells booleans t and f in text format. "true"/"false" is accepted on input but
        // is not what a client parsing a bool column expects to read back.
        bool b => Utf8(b ? "t" : "f"),

        // \x-prefixed hex is the modern bytea text format; the older escape format is ambiguous.
        byte[] bytes => Utf8("\\x" + Convert.ToHexString(bytes).ToLowerInvariant()),

        // Postgres timestamps use a space separator and no T, and a client matching the timestamp
        // OID will reject ISO-8601's T. Six fractional digits is Postgres's own microsecond
        // resolution — DuckDB's TIMESTAMP is microseconds too, so nothing is lost.
        DateTime dt => Utf8(dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)),
        DateTimeOffset dto => Utf8(dto.ToString("yyyy-MM-dd HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture)),
        DateOnly d => Utf8(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        TimeOnly t => Utf8(t.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture)),
        TimeSpan ts => Utf8(ts.ToString("c", CultureInfo.InvariantCulture)),

        // Round-trippable rather than shortest: a float column read back by a BI tool should be the
        // value DuckDB held, not a rendering of it.
        double dbl => Utf8(dbl.ToString("R", CultureInfo.InvariantCulture)),
        float f => Utf8(f.ToString("R", CultureInfo.InvariantCulture)),

        decimal or BigInteger or ulong => Utf8(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),

        string s => Utf8(s),

        // LIST, STRUCT, and MAP arrive as CLR collections and are declared as text. Rendering them
        // in Postgres's array literal syntax would be a lie for STRUCT and MAP, so they are rendered
        // readably and left as text.
        IDictionary dictionary => Utf8(RenderDictionary(dictionary)),
        IEnumerable sequence and not string => Utf8(RenderSequence(sequence)),

        _ => Utf8(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    private static string RenderSequence(IEnumerable sequence)
    {
        var builder = new StringBuilder("[");
        var first = true;

        foreach (var item in sequence)
        {
            if (!first)
            {
                builder.Append(", ");
            }

            builder.Append(RenderNested(item));
            first = false;
        }

        return builder.Append(']').ToString();
    }

    private static string RenderDictionary(IDictionary dictionary)
    {
        var builder = new StringBuilder("{");
        var first = true;

        foreach (DictionaryEntry entry in dictionary)
        {
            if (!first)
            {
                builder.Append(", ");
            }

            builder.Append(Convert.ToString(entry.Key, CultureInfo.InvariantCulture))
                .Append(": ")
                .Append(RenderNested(entry.Value));
            first = false;
        }

        return builder.Append('}').ToString();
    }

    private static string RenderNested(object? value)
    {
        var encoded = Encode(value);
        return encoded is null ? "NULL" : Encoding.UTF8.GetString(encoded);
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    /// <summary>PostgreSQL's epoch for date and timestamp binary values: 2000-01-01.</summary>
    private static readonly DateTime PostgresEpoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    private const long MicrosecondsPerTick = 10;

    /// <summary>
    ///     Renders a value in PostgreSQL's binary format for the given OID, or null for SQL NULL.
    /// </summary>
    /// <remarks>
    ///     Binary is not an optimisation here, it is a requirement. Npgsql — which Power BI's
    ///     PostgreSQL connector is built on — resolves a converter per column from the type OID and
    ///     the declared format, and for several of the types that matter most to a BI tool
    ///     (<c>int8</c>, <c>numeric</c>, timestamps) it has no text-format read path at all. A server
    ///     that declares those columns as text is not merely slower: the client refuses to read them.
    /// </remarks>
    public static byte[]? EncodeBinary(object? value, int oid)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        return oid switch
        {
            Bool => [Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? (byte)1 : (byte)0],
            Int2 => BigEndian(BitConverter.GetBytes(Convert.ToInt16(value, CultureInfo.InvariantCulture))),
            Int4 => BigEndian(BitConverter.GetBytes(Convert.ToInt32(value, CultureInfo.InvariantCulture))),
            Int8 => BigEndian(BitConverter.GetBytes(Convert.ToInt64(value, CultureInfo.InvariantCulture))),
            Float4 => BigEndian(BitConverter.GetBytes(Convert.ToSingle(value, CultureInfo.InvariantCulture))),
            Float8 => BigEndian(BitConverter.GetBytes(Convert.ToDouble(value, CultureInfo.InvariantCulture))),
            Numeric => EncodeNumeric(Convert.ToDecimal(value, CultureInfo.InvariantCulture)),
            Date => BigEndian(BitConverter.GetBytes(DaysSinceEpoch(value))),
            Time => BigEndian(BitConverter.GetBytes(MicrosecondsSinceMidnight(value))),
            Timestamp or Timestamptz => BigEndian(BitConverter.GetBytes(MicrosecondsSinceEpoch(value))),
            Interval => EncodeInterval(value),
            Uuid => ((Guid)value).ToByteArray(bigEndian: true),
            Bytea => (byte[])value,

            // Text and varchar are UTF-8 in both formats, so the text projection is already correct
            // and everything that fell back to text — wide integers, LIST, STRUCT, MAP — comes with
            // it rather than needing a second rendering.
            _ => Encode(value),
        };
    }

    private static byte[] BigEndian(byte[] bytes)
    {
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }

    private static int DaysSinceEpoch(object value) => value switch
    {
        DateOnly d => d.DayNumber - DateOnly.FromDateTime(PostgresEpoch).DayNumber,
        DateTime dt => (int)(dt.Date - PostgresEpoch.Date).TotalDays,
        _ => 0,
    };

    private static long MicrosecondsSinceMidnight(object value) => value switch
    {
        TimeOnly t => t.Ticks / MicrosecondsPerTick,
        TimeSpan ts => ts.Ticks / MicrosecondsPerTick,
        _ => 0,
    };

    private static long MicrosecondsSinceEpoch(object value) => value switch
    {
        DateTime dt => (dt.Ticks - PostgresEpoch.Ticks) / MicrosecondsPerTick,
        DateTimeOffset dto => (dto.UtcDateTime.Ticks - PostgresEpoch.Ticks) / MicrosecondsPerTick,
        _ => 0,
    };

    private static byte[] EncodeInterval(object value)
    {
        var span = value as TimeSpan? ?? TimeSpan.Zero;
        var buffer = new byte[16];

        // microseconds, days, months — days and months stay zero because a TimeSpan carries no
        // calendar component to put in them.
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 8), span.Ticks / MicrosecondsPerTick);
        if (BitConverter.IsLittleEndian)
        {
            buffer.AsSpan(0, 8).Reverse();
        }

        return buffer;
    }

    /// <summary>
    ///     Encodes a decimal in PostgreSQL's base-10000 numeric format.
    /// </summary>
    /// <remarks>
    ///     The layout is <c>ndigits · weight · sign · dscale</c> followed by <c>ndigits</c> base-10000
    ///     groups, most significant first. <c>weight</c> is the base-10000 exponent of the first
    ///     group, so it counts groups before the decimal point rather than digits — the detail that
    ///     makes a hand-rolled implementation render 1.5 as 15000 if it is read as digits.
    /// </remarks>
    private static byte[] EncodeNumeric(decimal value)
    {
        const int PositiveSign = 0x0000;
        const int NegativeSign = 0x4000;

        var negative = value < 0;
        var absolute = Math.Abs(value);
        var scale = (decimal.GetBits(absolute)[3] >> 16) & 0xFF;

        // Work on the unscaled integer, padded so the fractional digits fill whole base-10000
        // groups. Without the padding, a scale that is not a multiple of four shifts every
        // fractional digit by one position.
        var padding = (4 - (scale % 4)) % 4;
        var unscaled = new BigInteger(absolute * Pow10(scale)) * Pow10Big(padding);
        var fractionGroups = (scale + padding) / 4;

        var groups = new List<short>();
        if (unscaled.IsZero)
        {
            groups.Add(0);
        }

        while (!unscaled.IsZero)
        {
            unscaled = BigInteger.DivRem(unscaled, 10000, out var remainder);
            groups.Add((short)remainder);
        }

        groups.Reverse();
        var weight = groups.Count - fractionGroups - 1;

        // Trailing zero groups carry no information and Postgres omits them.
        while (groups.Count > 1 && groups[^1] == 0)
        {
            groups.RemoveAt(groups.Count - 1);
        }

        var buffer = new byte[8 + (groups.Count * 2)];
        var span = buffer.AsSpan();
        System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(span[..2], (short)groups.Count);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(span[2..4], (short)weight);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(span[4..6], (short)(negative ? NegativeSign : PositiveSign));
        System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(span[6..8], (short)scale);

        for (var i = 0; i < groups.Count; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(span.Slice(8 + (i * 2), 2), groups[i]);
        }

        return buffer;
    }

    private static decimal Pow10(int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++)
        {
            result *= 10m;
        }

        return result;
    }

    private static BigInteger Pow10Big(int exponent) => BigInteger.Pow(10, exponent);
}
