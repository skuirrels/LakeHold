using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Lakehold.Api.PublicApi;

/// <summary>Protected, request-bound cursor for monotonic DuckLake snapshot identifiers.</summary>
internal static class SnapshotCursor
{
    private const string Purpose = "LakeHold.PublicApi.SnapshotCursor.v1";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public static string Encode(
        IDataProtectionProvider provider,
        string scope,
        long upperSnapshotInclusive,
        long beforeSnapshotExclusive)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var protector = provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
        return protector.Protect(
            JsonSerializer.Serialize(new Payload(
                Version: 1,
                Scope: ScopeHash(scope),
                UpperSnapshotInclusive: upperSnapshotInclusive,
                BeforeSnapshotExclusive: beforeSnapshotExclusive)),
            Lifetime);
    }

    public static bool TryDecode(
        IDataProtectionProvider provider,
        string cursor,
        string scope,
        out SnapshotPosition position)
    {
        ArgumentNullException.ThrowIfNull(provider);
        position = default;
        try
        {
            var protector = provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
            var payload = JsonSerializer.Deserialize<Payload>(protector.Unprotect(cursor));
            var expected = Encoding.UTF8.GetBytes(ScopeHash(scope));
            var actual = Encoding.UTF8.GetBytes(payload?.Scope ?? string.Empty);
            if (payload is null
                || payload.Version != 1
                || payload.UpperSnapshotInclusive < 0
                || payload.BeforeSnapshotExclusive < 0
                || !CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                return false;
            }

            position = new SnapshotPosition(
                payload.UpperSnapshotInclusive,
                payload.BeforeSnapshotExclusive);
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return false;
        }
    }

    public static string Scope(
        string tenant,
        string catalog,
        DateTimeOffset? committedFromInclusive,
        DateTimeOffset? committedToInclusive)
        => string.Join(
            '\n',
            tenant,
            catalog,
            committedFromInclusive?.ToUniversalTime().ToString("O") ?? string.Empty,
            committedToInclusive?.ToUniversalTime().ToString("O") ?? string.Empty);

    private static string ScopeHash(string scope)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));

    private sealed record Payload(
        int Version,
        string Scope,
        long UpperSnapshotInclusive,
        long BeforeSnapshotExclusive);
}

internal readonly record struct SnapshotPosition(long UpperSnapshotInclusive, long BeforeSnapshotExclusive);
