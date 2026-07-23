using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Lakehold.ControlPlane.Model;

namespace Lakehold.ControlPlane.Security;

/// <summary>An issued token: the record to persist, and the plaintext shown to the caller exactly once.</summary>
/// <param name="Record">
///     The entity to save. It carries only the prefix and the hash — never the plaintext.
/// </param>
/// <param name="Plaintext">The full token string. Returned once at creation and not recoverable afterwards.</param>
public sealed record IssuedToken(ApiToken Record, string Plaintext);

/// <summary>
///     Generates and verifies API bearer tokens.
/// </summary>
/// <remarks>
///     <para>
///         Format is <c>lkh_&lt;tenant|admin&gt;_&lt;secret&gt;</c>, where the secret is 32 bytes from
///         <see cref="RandomNumberGenerator"/> as unpadded base64url (43 characters). The full token is
///         hashed with SHA-256 and only the hash is stored, so reading the table never yields a usable
///         credential.
///     </para>
///     <para>
///         SHA-256 rather than a slow password KDF is deliberate: a 256-bit random secret has no brute
///         force to defend against, and a per-request KDF would be a self-inflicted denial of service.
///         Verification is constant-time. The reasoning, and the phased plan this belongs to, are in
///         <c>docs/AUTHENTICATION.md</c>.
///     </para>
/// </remarks>
public static class ApiTokenFactory
{
    /// <summary>The reserved slug whose prefix marks an instance-scoped token.</summary>
    public const string InstanceSlug = "admin";

    private const string Scheme = "lkh";
    private const int SecretBytes = 32;

    /// <summary>
    ///     Issues a token. For <see cref="TokenScope.Tenant"/>, <paramref name="tenant"/> is required
    ///     and its slug forms the prefix; for <see cref="TokenScope.Instance"/> it must be null and the
    ///     prefix is <c>lkh_admin_</c>. <paramref name="catalogName"/> and <paramref name="readOnly"/>
    ///     are only meaningful for a tenant token.
    /// </summary>
    public static IssuedToken Issue(
        TokenScope scope,
        Tenant? tenant,
        string name,
        DateTimeOffset createdUtc,
        bool readOnly = false,
        string? catalogName = null,
        DateTimeOffset? expiresUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var isInstance = scope == TokenScope.Instance;
        var prefix = PrefixFor(SlugFor(scope, tenant));

        Span<byte> secret = stackalloc byte[SecretBytes];
        RandomNumberGenerator.Fill(secret);
        var plaintext = prefix + Base64Url.EncodeToString(secret);

        var record = new ApiToken
        {
            Scope = scope,
            TenantId = isInstance ? null : tenant!.Id,
            CatalogName = isInstance ? null : catalogName,
            Name = name,
            Prefix = prefix,
            SecretHash = Hash(plaintext),
            ReadOnly = !isInstance && readOnly,
            CreatedUtc = createdUtc,
            ExpiresUtc = expiresUtc,
        };

        return new IssuedToken(record, plaintext);
    }

    /// <summary>SHA-256 of a token, lower-hex — the stored form.</summary>
    public static string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    /// <summary>
    ///     Verifies a presented token against a stored hash in constant time. Returns false — rather
    ///     than throwing — for empty inputs or a malformed stored hash, so a corrupt row cannot become
    ///     an exception on the authentication path.
    /// </summary>
    public static bool Verify(string? presentedToken, string? storedHash)
    {
        if (string.IsNullOrEmpty(presentedToken) || string.IsNullOrEmpty(storedHash))
        {
            return false;
        }

        // Compare the lower-hex forms in constant time. FixedTimeEquals returns false when the lengths
        // differ, so a malformed stored hash is simply a non-match rather than an exception.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Hash(presentedToken)),
            Encoding.UTF8.GetBytes(storedHash));
    }

    /// <summary>
    ///     Extracts the <c>lkh_&lt;tenant&gt;_</c> lookup prefix from a presented token, or false if it
    ///     is not a well-formed Lakehold token. Used to narrow the candidate set before verifying.
    /// </summary>
    public static bool TryGetPrefix(string? presentedToken, out string prefix)
    {
        prefix = string.Empty;
        if (string.IsNullOrEmpty(presentedToken)
            || !presentedToken.StartsWith(Scheme + "_", StringComparison.Ordinal))
        {
            return false;
        }

        // scheme _ subject _ secret. The prefix is everything up to and including the second
        // underscore; a non-empty subject and a non-empty secret are both required.
        var secondUnderscore = presentedToken.IndexOf('_', Scheme.Length + 1);
        if (secondUnderscore <= Scheme.Length + 1 || secondUnderscore >= presentedToken.Length - 1)
        {
            return false;
        }

        prefix = presentedToken[..(secondUnderscore + 1)];
        return true;
    }

    private static string SlugFor(TokenScope scope, Tenant? tenant)
    {
        if (scope == TokenScope.Instance)
        {
            if (tenant is not null)
            {
                throw new ArgumentException("An instance-scoped token belongs to no tenant.", nameof(tenant));
            }

            return InstanceSlug;
        }

        ArgumentNullException.ThrowIfNull(tenant);

        // A tenant named 'admin' would mint tokens indistinguishable from instance-scoped ones. Tenant
        // creation rejects the slug (see AUTHENTICATION.md); this is defence in depth at the one place
        // the prefix is actually formed.
        if (string.Equals(tenant.Slug, InstanceSlug, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{InstanceSlug}' is a reserved slug.", nameof(tenant));
        }

        return tenant.Slug;
    }

    private static string PrefixFor(string slug) => $"{Scheme}_{slug}_";
}
