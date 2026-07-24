using Microsoft.EntityFrameworkCore;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;

namespace Lakehold.Api;

// CA1848: source-generated logging exists for hot paths. Bootstrap runs once at start-up, so the
// delegates would add ceremony for no measurable gain.
#pragma warning disable CA1848

/// <summary>
///     Solves the chicken-and-egg problem: if the API needs a token, where does the first one come
///     from? On first start with no tokens in the database, an instance-scoped token is minted and
///     written to the log exactly once. It is instance-scoped because a fresh production node has no
///     tenant for it to belong to, and minting the first tenant is the job it exists for.
/// </summary>
/// <remarks>
///     The mint runs only when the token table is empty, so it cannot be used to add a second admin
///     credential to a running deployment. <c>Lakehold__BootstrapToken</c> overrides it for
///     deployments that provision credentials externally and cannot scrape a log.
/// </remarks>
internal static class TokenBootstrap
{
    public static async Task EnsureBootstrapTokenAsync(
        IServiceProvider services,
        string? overrideToken,
        ILogger logger,
        TimeProvider clock)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();

        // Fail open on a missing table: the additive-schema step that creates ApiTokens may not have
        // run on an older database yet, and taking the node down for it would be worse than starting
        // without a bootstrap token an operator can also supply out of band.
        try
        {
            if (await context.ApiTokens.AnyAsync().ConfigureAwait(false))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not check for existing API tokens; skipping bootstrap.");
            return;
        }

        var now = clock.GetUtcNow();
        var instancePrefix = $"lkh_{ApiTokenFactory.InstanceSlug}_";

        if (!string.IsNullOrWhiteSpace(overrideToken))
        {
            if (ApiTokenFactory.TryGetPrefix(overrideToken, out var prefix)
                && string.Equals(prefix, instancePrefix, StringComparison.Ordinal))
            {
                context.ApiTokens.Add(new ApiToken
                {
                    Scope = TokenScope.Instance,
                    Name = "bootstrap",
                    Prefix = prefix,
                    SecretHash = ApiTokenFactory.Hash(overrideToken),
                    CreatedUtc = now,
                });
                await context.SaveChangesAsync().ConfigureAwait(false);

                // The operator supplied this value; it is deliberately not echoed to the log.
                logger.LogInformation("Bootstrap instance token configured from Lakehold__BootstrapToken.");
                return;
            }

            logger.LogWarning(
                "Lakehold__BootstrapToken is not a well-formed {Prefix}… token; minting one instead.",
                instancePrefix);
        }

        var issued = ApiTokenFactory.Issue(TokenScope.Instance, tenant: null, "bootstrap", now);
        context.ApiTokens.Add(issued.Record);
        await context.SaveChangesAsync().ConfigureAwait(false);

        // The one intentional exception to "never log a credential": this is the only time this token
        // can be recovered, it authenticates provisioning only (not data), and the alternative is a
        // deployment that cannot be administered at all. Logged once, and never again.
        logger.LogWarning(
            "No API tokens existed, so a bootstrap instance token was minted. It is shown ONCE and "
            + "cannot be recovered — store it now: {Token}",
            issued.Plaintext);
    }
}
#pragma warning restore CA1848
