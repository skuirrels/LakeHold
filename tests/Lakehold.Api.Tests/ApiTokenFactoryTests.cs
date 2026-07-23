using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Cover for token generation and verification: the format is what it claims, the plaintext is
///     never persisted, verification is exact, and the malformed inputs an auth path will actually see
///     are refused rather than thrown.
/// </summary>
public sealed class ApiTokenFactoryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static Tenant Tenant(string slug, int id = 1) =>
        new() { Id = id, Slug = slug, DisplayName = slug, CreatedUtc = Now };

    [Fact]
    public void Tenant_token_carries_the_tenant_prefix_and_only_a_hash()
    {
        var issued = ApiTokenFactory.Issue(TokenScope.Tenant, Tenant("demo", 7), "bi", Now);

        Assert.StartsWith("lkh_demo_", issued.Plaintext, StringComparison.Ordinal);
        Assert.Equal("lkh_demo_", issued.Record.Prefix);
        Assert.Equal(TokenScope.Tenant, issued.Record.Scope);
        Assert.Equal(7, issued.Record.TenantId);

        // "lkh_demo_" (9) + 43 base64url chars from 32 random bytes.
        Assert.Equal(9 + 43, issued.Plaintext.Length);

        // The record stores the hash, never the plaintext.
        Assert.NotEqual(issued.Plaintext, issued.Record.SecretHash);
        Assert.Equal(ApiTokenFactory.Hash(issued.Plaintext), issued.Record.SecretHash);
        Assert.Equal(64, issued.Record.SecretHash.Length);
    }

    [Fact]
    public void Instance_token_uses_the_admin_prefix_and_no_tenant()
    {
        // catalogName and readOnly are ignored for an instance token.
        var issued = ApiTokenFactory.Issue(
            TokenScope.Instance, tenant: null, "bootstrap", Now, readOnly: true, catalogName: "analytics");

        Assert.StartsWith("lkh_admin_", issued.Plaintext, StringComparison.Ordinal);
        Assert.Equal("lkh_admin_", issued.Record.Prefix);
        Assert.Null(issued.Record.TenantId);
        Assert.Null(issued.Record.CatalogName);
        Assert.False(issued.Record.ReadOnly);
    }

    [Fact]
    public void Catalog_narrowing_and_read_only_flow_onto_a_tenant_token()
    {
        var issued = ApiTokenFactory.Issue(
            TokenScope.Tenant, Tenant("demo"), "dashboard", Now, readOnly: true, catalogName: "analytics");

        Assert.Equal("analytics", issued.Record.CatalogName);
        Assert.True(issued.Record.ReadOnly);
    }

    [Fact]
    public void Two_tokens_are_different()
    {
        var a = ApiTokenFactory.Issue(TokenScope.Tenant, Tenant("demo"), "one", Now);
        var b = ApiTokenFactory.Issue(TokenScope.Tenant, Tenant("demo"), "two", Now);

        Assert.NotEqual(a.Plaintext, b.Plaintext);
        Assert.NotEqual(a.Record.SecretHash, b.Record.SecretHash);
    }

    [Fact]
    public void Verify_accepts_the_plaintext_and_rejects_anything_else()
    {
        var issued = ApiTokenFactory.Issue(TokenScope.Tenant, Tenant("demo"), "bi", Now);

        Assert.True(ApiTokenFactory.Verify(issued.Plaintext, issued.Record.SecretHash));
        Assert.False(ApiTokenFactory.Verify(issued.Plaintext + "x", issued.Record.SecretHash));
        Assert.False(ApiTokenFactory.Verify("lkh_demo_not-the-real-secret", issued.Record.SecretHash));
    }

    [Fact]
    public void Hash_is_deterministic_and_lower_hex()
    {
        const string token = "lkh_demo_abcdefghijklmnopqrstuvwxyz0123456789-_ABCDE";

        var hash = ApiTokenFactory.Hash(token);

        Assert.Equal(ApiTokenFactory.Hash(token), hash);
        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.Contains(c, "0123456789abcdef"));
    }

    [Fact]
    public void Verify_refuses_empty_or_malformed_inputs_rather_than_throwing()
    {
        var validHash = ApiTokenFactory.Hash("lkh_demo_secret");

        Assert.False(ApiTokenFactory.Verify(null, validHash));
        Assert.False(ApiTokenFactory.Verify("", validHash));
        Assert.False(ApiTokenFactory.Verify("lkh_demo_secret", null));
        Assert.False(ApiTokenFactory.Verify("lkh_demo_secret", ""));

        // A corrupt stored hash (wrong length, non-hex) must be a false, not an exception.
        Assert.False(ApiTokenFactory.Verify("lkh_demo_secret", "not-hex"));
        Assert.False(ApiTokenFactory.Verify("lkh_demo_secret", "zz"));
    }

    [Theory]
    [InlineData("lkh_demo_secret", true, "lkh_demo_")]
    [InlineData("lkh_admin_secret", true, "lkh_admin_")]
    [InlineData("lkh_a_b", true, "lkh_a_")]
    [InlineData("lkh_demo_", false, "")]      // empty secret
    [InlineData("lkh__secret", false, "")]    // empty subject
    [InlineData("lkh_demo", false, "")]       // no second underscore
    [InlineData("bearer_demo_secret", false, "")]
    [InlineData("", false, "")]
    public void TryGetPrefix_parses_valid_tokens_and_refuses_the_rest(string token, bool ok, string expected)
    {
        Assert.Equal(ok, ApiTokenFactory.TryGetPrefix(token, out var prefix));
        Assert.Equal(expected, prefix);
    }

    [Fact]
    public void Issue_refuses_a_scope_and_subject_that_disagree()
    {
        // An instance token with a tenant, and a tenant token without one.
        Assert.Throws<ArgumentException>(() =>
            ApiTokenFactory.Issue(TokenScope.Instance, Tenant("demo"), "x", Now));
        Assert.Throws<ArgumentNullException>(() =>
            ApiTokenFactory.Issue(TokenScope.Tenant, tenant: null, "x", Now));
    }

    [Fact]
    public void Issue_refuses_a_tenant_named_admin()
    {
        // The reserved slug would produce a prefix indistinguishable from an instance token's.
        Assert.Throws<ArgumentException>(() =>
            ApiTokenFactory.Issue(TokenScope.Tenant, Tenant("admin"), "x", Now));
    }
}
