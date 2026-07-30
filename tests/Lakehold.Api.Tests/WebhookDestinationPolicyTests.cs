using Lakehold.Api.Cdc;
using Xunit;

namespace Lakehold.Api.Tests;

public sealed class WebhookDestinationPolicyTests
{
    [Theory]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://10.0.0.1/hook")]
    [InlineData("https://172.16.0.1/hook")]
    [InlineData("https://192.168.1.1/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://[::1]/hook")]
    [InlineData("https://[fc00::1]/hook")]
    public async Task Private_and_metadata_destinations_are_refused(string url)
    {
        var error = await WebhookDestinationPolicy.ValidateAsync(
            new Uri(url),
            new CdcOptions(),
            CancellationToken.None);

        Assert.Contains("prohibited", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Http_requires_an_explicit_development_opt_in()
    {
        var endpoint = new Uri("http://93.184.216.34/hook");

        Assert.Contains(
            "https",
            await WebhookDestinationPolicy.ValidateAsync(endpoint, new CdcOptions(), CancellationToken.None),
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(await WebhookDestinationPolicy.ValidateAsync(
            endpoint,
            new CdcOptions { AllowHttp = true },
            CancellationToken.None));
    }

    [Fact]
    public async Task Host_allowlist_accepts_exact_and_wildcard_hosts_only()
    {
        var options = new CdcOptions
        {
            AllowUnsafeDestinations = true,
            AllowedHosts = ["hooks.example.com", "*.trusted.example"],
        };

        Assert.Null(await WebhookDestinationPolicy.ValidateAsync(
            new Uri("https://hooks.example.com/cdc"),
            options,
            CancellationToken.None));
        Assert.Null(await WebhookDestinationPolicy.ValidateAsync(
            new Uri("https://tenant.trusted.example/cdc"),
            options,
            CancellationToken.None));
        Assert.NotNull(await WebhookDestinationPolicy.ValidateAsync(
            new Uri("https://example.com/cdc"),
            options,
            CancellationToken.None));
    }

    [Fact]
    public async Task Embedded_endpoint_credentials_are_refused()
    {
        var error = await WebhookDestinationPolicy.ValidateAsync(
            new Uri("https://user:secret@93.184.216.34/hook"),
            new CdcOptions(),
            CancellationToken.None);

        Assert.Contains("credentials", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Safe_resolution_returns_the_address_the_socket_must_use()
    {
        var resolution = await WebhookDestinationPolicy.ResolveAsync(
            new Uri("https://93.184.216.34/hook"),
            new CdcOptions(),
            CancellationToken.None);

        Assert.Null(resolution.Error);
        Assert.Equal("93.184.216.34", resolution.Address?.ToString());
    }
}
