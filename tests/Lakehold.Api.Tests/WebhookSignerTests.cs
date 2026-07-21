using System.Text;
using Lakehold.Api.Cdc;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Cover for the delivery signature: deterministic, verifiable, and actually sensitive to both
///     the body and the key — the properties a receiver's authentication depends on.
/// </summary>
public sealed class WebhookSignerTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"catalog":"analytics","fromSnapshot":3}""");

    [Fact]
    public void Signature_is_deterministic_and_verifies()
    {
        var first = WebhookSigner.Compute(Body, "a-signing-secret-of-adequate-length");
        var second = WebhookSigner.Compute(Body, "a-signing-secret-of-adequate-length");

        Assert.Equal(first, second);
        Assert.StartsWith("sha256=", first, StringComparison.Ordinal);
        Assert.True(WebhookSigner.Verify(Body, "a-signing-secret-of-adequate-length", first));
    }

    [Fact]
    public void Signature_rejects_a_tampered_body()
    {
        var signature = WebhookSigner.Compute(Body, "a-signing-secret-of-adequate-length");
        var tampered = Encoding.UTF8.GetBytes("""{"catalog":"analytics","fromSnapshot":4}""");

        Assert.False(WebhookSigner.Verify(tampered, "a-signing-secret-of-adequate-length", signature));
    }

    [Fact]
    public void Signature_rejects_a_different_key()
    {
        var signature = WebhookSigner.Compute(Body, "a-signing-secret-of-adequate-length");

        Assert.False(WebhookSigner.Verify(Body, "an-entirely-different-secret-key", signature));
    }

    [Fact]
    public void Verify_refuses_missing_inputs_rather_than_throwing()
    {
        Assert.False(WebhookSigner.Verify(Body, "a-signing-secret-of-adequate-length", null));
        Assert.False(WebhookSigner.Verify(Body, "a-signing-secret-of-adequate-length", string.Empty));
        Assert.False(WebhookSigner.Verify(Body, string.Empty, "sha256=abc"));
    }
}
