using Lakehold.Api.Security;

namespace Lakehold.Api.Cdc;

/// <summary>Validates a webhook URI at creation and again immediately before every delivery.</summary>
public static class WebhookDestinationPolicy
{
    /// <summary>Returns a client-safe validation error, or null when the destination is allowed.</summary>
    public static async Task<string?> ValidateAsync(
        Uri endpoint,
        CdcOptions options,
        CancellationToken cancellationToken)
        => (await ResolveAsync(endpoint, options, cancellationToken).ConfigureAwait(false)).Error;

    /// <summary>
    ///     Validates a destination and returns the public address that the HTTP connection must use.
    ///     Returning the resolved address closes the DNS-rebinding gap between policy evaluation and
    ///     socket connection.
    /// </summary>
    internal static Task<OutboundDestinationResolution> ResolveAsync(
        Uri endpoint,
        CdcOptions options,
        CancellationToken cancellationToken) => OutboundDestinationPolicy.ResolveAsync(
            endpoint,
            options,
            "Webhook",
            cancellationToken);
}
