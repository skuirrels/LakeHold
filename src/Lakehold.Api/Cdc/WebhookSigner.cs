using System.Security.Cryptography;
using System.Text;

namespace Lakehold.Api.Cdc;

/// <summary>
///     Signs webhook bodies so a receiver can authenticate that a delivery came from this deployment
///     and arrived unaltered.
/// </summary>
/// <remarks>
///     The current scheme is HMAC-SHA256 over a versioned envelope containing the creation
///     timestamp, durable delivery id, and exact request body. Binding all three prevents an
///     otherwise valid body from being relabelled as another delivery. The secret is per
///     subscription: a receiver compromise burns one subscription's key, not every tenant's.
/// </remarks>
public static class WebhookSigner
{
    /// <summary>Header carrying the body signature.</summary>
    public const string SignatureHeader = "X-Lakehold-Signature";

    /// <summary>Header carrying the stable id of one logical delivery, for receiver-side dedup.</summary>
    public const string DeliveryHeader = "X-Lakehold-Delivery";

    /// <summary>Header carrying the signed envelope creation time as Unix seconds.</summary>
    public const string TimestampHeader = "X-Lakehold-Timestamp";

    /// <summary>Header identifying the signing-base format.</summary>
    public const string SignatureVersionHeader = "X-Lakehold-Signature-Version";

    /// <summary>Current signing-base format identifier.</summary>
    public const string SignatureVersion = "v1";

    /// <summary>Computes the signature header value for <paramref name="body"/>.</summary>
    public static string Compute(ReadOnlySpan<byte> body, string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        Span<byte> hash = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body, hash);
        return $"sha256={Convert.ToHexStringLower(hash)}";
    }

    /// <summary>Computes a signature over a timestamped base and the exact body bytes.</summary>
    public static string Compute(ReadOnlySpan<byte> body, string secret, long timestamp)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        var prefix = Encoding.UTF8.GetBytes($"{timestamp}.");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed.AsSpan(prefix.Length));
        return Compute(signed, secret);
    }

    /// <summary>
    ///     Computes the current signature over
    ///     <c>v1.&lt;timestamp&gt;.&lt;delivery-id&gt;.&lt;exact-body-bytes&gt;</c>.
    /// </summary>
    public static string Compute(
        ReadOnlySpan<byte> body,
        string secret,
        long timestamp,
        string deliveryId)
    {
        ArgumentException.ThrowIfNullOrEmpty(deliveryId);

        var prefix = Encoding.UTF8.GetBytes($"{SignatureVersion}.{timestamp}.{deliveryId}.");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed.AsSpan(prefix.Length));
        return Compute(signed, secret);
    }

    /// <summary>
    ///     Verifies a received signature header against <paramref name="body"/>. Provided for
    ///     receivers built on this assembly and for tests; the dispatcher itself only signs.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> body, string secret, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signatureHeader))
        {
            return false;
        }

        var expected = Compute(body, secret);

        // Fixed-time comparison, because a signature check that leaks timing can be ground out one
        // byte at a time by anyone who can send requests.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signatureHeader));
    }

    /// <summary>
    ///     Verifies the timestamped signature and rejects envelopes outside the allowed clock skew.
    /// </summary>
    public static bool Verify(
        ReadOnlySpan<byte> body,
        string secret,
        string? signatureHeader,
        long timestamp,
        TimeProvider clock,
        TimeSpan allowedAge)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(allowedAge, TimeSpan.Zero);

        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signatureHeader))
        {
            return false;
        }

        var now = clock.GetUtcNow();
        DateTimeOffset created;
        try
        {
            created = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if (created > now + allowedAge || created < now - allowedAge)
        {
            return false;
        }

        var expected = Compute(body, secret, timestamp);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signatureHeader));
    }

    /// <summary>
    ///     Verifies the current versioned envelope and rejects an unknown version or stale creation
    ///     timestamp.
    /// </summary>
    public static bool Verify(
        ReadOnlySpan<byte> body,
        string secret,
        string? signatureHeader,
        string? signatureVersion,
        long timestamp,
        string? deliveryId,
        TimeProvider clock,
        TimeSpan allowedAge)
    {
        if (!string.Equals(signatureVersion, SignatureVersion, StringComparison.Ordinal)
            || string.IsNullOrEmpty(deliveryId)
            || !IsFresh(timestamp, clock, allowedAge))
        {
            return false;
        }

        var expected = Compute(body, secret, timestamp, deliveryId);
        return FixedTimeEquals(expected, signatureHeader);
    }

    private static bool IsFresh(long timestamp, TimeProvider clock, TimeSpan allowedAge)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(allowedAge, TimeSpan.Zero);

        DateTimeOffset created;
        try
        {
            created = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        var now = clock.GetUtcNow();
        return created <= now + allowedAge && created >= now - allowedAge;
    }

    private static bool FixedTimeEquals(string expected, string? actual)
        => !string.IsNullOrEmpty(actual)
           && CryptographicOperations.FixedTimeEquals(
               Encoding.UTF8.GetBytes(expected),
               Encoding.UTF8.GetBytes(actual));
}
