using System.Buffers.Binary;
using System.Text;

namespace Lakehold.Api.PgWire;

/// <summary>Backend message type tags, as the protocol spells them.</summary>
internal static class PgBackend
{
    public const byte Authentication = (byte)'R';
    public const byte BackendKeyData = (byte)'K';
    public const byte BindComplete = (byte)'2';
    public const byte CloseComplete = (byte)'3';
    public const byte CommandComplete = (byte)'C';
    public const byte DataRow = (byte)'D';
    public const byte EmptyQueryResponse = (byte)'I';
    public const byte ErrorResponse = (byte)'E';
    public const byte NoData = (byte)'n';
    public const byte NoticeResponse = (byte)'N';
    public const byte ParameterDescription = (byte)'t';
    public const byte ParameterStatus = (byte)'S';
    public const byte ParseComplete = (byte)'1';
    public const byte ReadyForQuery = (byte)'Z';
    public const byte RowDescription = (byte)'T';
}

/// <summary>Frontend message type tags.</summary>
internal static class PgFrontend
{
    public const byte Bind = (byte)'B';
    public const byte Close = (byte)'C';
    public const byte Describe = (byte)'D';
    public const byte Execute = (byte)'E';
    public const byte Flush = (byte)'H';
    public const byte Parse = (byte)'P';
    public const byte PasswordMessage = (byte)'p';
    public const byte Query = (byte)'Q';
    public const byte Sync = (byte)'S';
    public const byte Terminate = (byte)'X';
}

/// <summary>
///     Builds backend messages into a growable buffer.
/// </summary>
/// <remarks>
///     Every backend message is <c>tag · int32 length · body</c>, where the length counts itself but
///     not the tag. The length is therefore only known once the body is written, so each message
///     reserves four bytes and back-fills them — which is the one detail that makes a hand-rolled
///     encoder worth isolating and testing on its own.
/// </remarks>
internal sealed class PgMessageWriter
{
    private byte[] _buffer = new byte[4096];
    private int _position;
    private int _messageStart = -1;

    /// <summary>Bytes written so far.</summary>
    public int Length => _position;

    /// <summary>The written bytes, ready for the socket.</summary>
    public ReadOnlyMemory<byte> Written => _buffer.AsMemory(0, _position);

    /// <summary>Discards everything written, keeping the buffer for reuse.</summary>
    public void Reset()
    {
        _position = 0;
        _messageStart = -1;
    }

    /// <summary>Starts a message with the given tag, reserving space for its length.</summary>
    public PgMessageWriter Begin(byte tag)
    {
        EnsureCapacity(5);
        _buffer[_position++] = tag;
        _messageStart = _position;
        _position += 4;
        return this;
    }

    /// <summary>Back-fills the length of the message opened by <see cref="Begin"/>.</summary>
    public PgMessageWriter End()
    {
        if (_messageStart < 0)
        {
            throw new InvalidOperationException("End called without a matching Begin.");
        }

        BinaryPrimitives.WriteInt32BigEndian(
            _buffer.AsSpan(_messageStart, 4),
            _position - _messageStart);

        _messageStart = -1;
        return this;
    }

    public PgMessageWriter Byte(byte value)
    {
        EnsureCapacity(1);
        _buffer[_position++] = value;
        return this;
    }

    public PgMessageWriter Int16(short value)
    {
        EnsureCapacity(2);
        BinaryPrimitives.WriteInt16BigEndian(_buffer.AsSpan(_position, 2), value);
        _position += 2;
        return this;
    }

    public PgMessageWriter Int32(int value)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteInt32BigEndian(_buffer.AsSpan(_position, 4), value);
        _position += 4;
        return this;
    }

    /// <summary>Writes a null-terminated UTF-8 string.</summary>
    public PgMessageWriter String(string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        EnsureCapacity(byteCount + 1);
        Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_position, byteCount));
        _position += byteCount;
        _buffer[_position++] = 0;
        return this;
    }

    public PgMessageWriter Bytes(ReadOnlySpan<byte> value)
    {
        EnsureCapacity(value.Length);
        value.CopyTo(_buffer.AsSpan(_position, value.Length));
        _position += value.Length;
        return this;
    }

    /// <summary>
    ///     Writes a length-prefixed field value, or the -1 sentinel that the protocol uses for NULL.
    /// </summary>
    public PgMessageWriter Field(ReadOnlySpan<byte> value, bool isNull)
    {
        if (isNull)
        {
            return Int32(-1);
        }

        Int32(value.Length);
        return Bytes(value);
    }

    private void EnsureCapacity(int extra)
    {
        if (_position + extra <= _buffer.Length)
        {
            return;
        }

        var target = _buffer.Length;
        while (target < _position + extra)
        {
            target *= 2;
        }

        Array.Resize(ref _buffer, target);
    }
}

/// <summary>Reads primitives out of a received message body.</summary>
internal ref struct PgMessageReader(ReadOnlySpan<byte> body)
{
    private readonly ReadOnlySpan<byte> _body = body;
    private int _position;

    public readonly bool IsAtEnd => _position >= _body.Length;

    public readonly int Remaining => _body.Length - _position;

    public byte Byte() => _body[_position++];

    public short Int16()
    {
        var value = BinaryPrimitives.ReadInt16BigEndian(_body[_position..]);
        _position += 2;
        return value;
    }

    public int Int32()
    {
        var value = BinaryPrimitives.ReadInt32BigEndian(_body[_position..]);
        _position += 4;
        return value;
    }

    /// <summary>Reads a null-terminated UTF-8 string.</summary>
    public string String()
    {
        var end = _body[_position..].IndexOf((byte)0);
        if (end < 0)
        {
            throw new InvalidDataException("Unterminated string in message body.");
        }

        var value = Encoding.UTF8.GetString(_body.Slice(_position, end));
        _position += end + 1;
        return value;
    }

    /// <summary>Skips a length-prefixed value, returning whether it was null.</summary>
    public bool SkipField()
    {
        var length = Int32();
        if (length < 0)
        {
            return true;
        }

        _position += length;
        return false;
    }
}
