using System.Security.Cryptography;
using System.Text;

namespace Lakehold.Api.Cdc;

/// <summary>
///     Signs webhook bodies so a receiver can authenticate that a delivery came from this deployment
///     and arrived unaltered.
/// </summary>
/// <remarks>
///     The scheme is the de facto webhook convention — an HMAC-SHA256 over the exact request body,
///     carried as <c>sha256=&lt;hex&gt;</c> in the <see cref="SignatureHeader"/> header — so existing
///     receiver middleware for GitHub-style webhooks verifies Lakehold deliveries unchanged. The
///     secret is per subscription: a receiver compromise burns one subscription's key, not every
///     tenant's.
/// </remarks>
public static class WebhookSigner
{
    /// <summary>Header carrying the body signature.</summary>
    public const string SignatureHeader = "X-Lakehold-Signature";

    /// <summary>Header carrying the unique id of one delivery attempt, for receiver-side dedup.</summary>
    public const string DeliveryHeader = "X-Lakehold-Delivery";

    /// <summary>Computes the signature header value for <paramref name="body"/>.</summary>
    public static string Compute(ReadOnlySpan<byte> body, string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        Span<byte> hash = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body, hash);
        return $"sha256={Convert.ToHexStringLower(hash)}";
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
}
