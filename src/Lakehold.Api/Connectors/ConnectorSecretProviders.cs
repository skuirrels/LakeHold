using System.Net.Http.Headers;
using System.Text.Json;
using Lakehold.Api.Security;
using Lakehold.ControlPlane.Model;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.Connectors;

/// <summary>Resolves a secret reference without exposing its value to domain or transport DTOs.</summary>
internal interface IConnectorSecretProvider
{
    string Scheme { get; }

    Task<string> ResolveAsync(string name, CancellationToken cancellationToken);
}

/// <summary>Selects an approved provider from the URI-like reference stored in a connector.</summary>
internal sealed class ConnectorSecretResolver(
    IEnumerable<IConnectorSecretProvider> providers,
    IOptions<ConnectorOptions> options)
{
    private readonly Dictionary<string, IConnectorSecretProvider> _providers = providers
        .ToDictionary(provider => provider.Scheme, StringComparer.OrdinalIgnoreCase);

    public async Task<string> ResolveAsync(
        string reference,
        string tenantSlug,
        string catalogName,
        string destinationHost,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (!ConnectorSecretAccessPolicy.IsAllowed(
                options.Value,
                tenantSlug,
                catalogName,
                reference,
                destinationHost))
        {
            throw new InvalidOperationException(
                "The connector secret reference is not bound to this tenant, catalog, and destination host.");
        }

        var separator = reference.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0 || separator == reference.Length - 3)
        {
            throw new InvalidOperationException("Connector secret references must use an approved provider scheme.");
        }

        var scheme = reference[..separator];
        var name = reference[(separator + 3)..];
        if (name.Length > 512 || name.Any(char.IsControl))
        {
            throw new InvalidOperationException("Connector secret references contain an invalid provider key.");
        }
        if (!_providers.TryGetValue(scheme, out var provider))
        {
            throw new InvalidOperationException($"Connector secret provider '{scheme}' is not registered.");
        }

        var secret = await provider.ResolveAsync(name, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("The connector secret provider returned an empty value.");
        }

        return secret;
    }
}

/// <summary>Fail-closed authorization for operator-owned connector credentials.</summary>
internal static class ConnectorSecretAccessPolicy
{
    public static bool IsAllowed(
        ConnectorOptions options,
        string tenantSlug,
        string catalogName,
        string reference,
        string destinationHost)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.SecretBindings.Any(binding =>
            string.Equals(binding.TenantSlug, tenantSlug, StringComparison.OrdinalIgnoreCase)
            && string.Equals(binding.CatalogName, catalogName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(binding.Reference, reference, StringComparison.Ordinal)
            && string.Equals(binding.DestinationHost, destinationHost, StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<string> References(DataConnectorAuthentication authentication)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        return new[]
            {
                authentication.SecretReference,
                authentication.UsernameSecretReference,
                authentication.PasswordSecretReference,
                authentication.ClientIdSecretReference,
                authentication.ClientSecretReference,
                authentication.RefreshTokenSecretReference,
                authentication.ClientCertificateSecretReference,
                authentication.CertificatePasswordSecretReference,
                authentication.SchemaRegistryUsernameSecretReference,
                authentication.SchemaRegistryPasswordSecretReference,
            }
            .Where(reference => reference is not null)
            .Select(reference => reference!);
    }
}

/// <summary>Compatibility provider for deployments that inject secrets into worker environments.</summary>
internal sealed class EnvironmentConnectorSecretProvider : IConnectorSecretProvider
{
    public string Scheme => "env";

    public Task<string> ResolveAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
        {
            throw new InvalidOperationException("Environment secret references contain invalid characters.");
        }

        return Task.FromResult(Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException("A referenced connector secret is not available on this worker."));
    }
}

/// <summary>
///     External secret-store adapter. The configured HTTPS service receives only the reference name
///     and returns <c>{"value":"..."}</c>; its bearer credential remains an environment secret.
/// </summary>
internal sealed class VaultConnectorSecretProvider(
    IHttpClientFactory clients,
    IOptions<ConnectorOptions> options) : IConnectorSecretProvider
{
    public const string HttpClientName = "lakehold-connector-vault";

    public string Scheme => "vault";

    public async Task<string> ResolveAsync(string name, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.SecretProviderEndpoint))
        {
            throw new InvalidOperationException("The external connector secret provider is not configured.");
        }

        var baseUri = new Uri(settings.SecretProviderEndpoint, UriKind.Absolute);
        var endpoint = new Uri(baseUri, $"secrets/{Uri.EscapeDataString(name)}");
        var resolution = await OutboundDestinationPolicy.ResolveAsync(
                endpoint,
                settings,
                "Connector secret provider",
                cancellationToken)
            .ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            throw new InvalidOperationException(resolution.Error);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (resolution.Address is not null)
        {
            request.Options.Set(OutboundConnection.ApprovedAddress, resolution.Address);
        }

        if (settings.SecretProviderTokenEnvironmentVariable is { Length: > 0 } variable)
        {
            var token = Environment.GetEnvironmentVariable(variable)
                ?? throw new InvalidOperationException("The external secret-provider credential is unavailable.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.RequestTimeout);
        using var response = await clients.CreateClient(HttpClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        const int maxResponseBytes = 1024 * 1024;
        if (response.Content.Headers.ContentLength > maxResponseBytes)
        {
            throw new InvalidOperationException("The external secret provider response exceeded its limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var limited = new LimitedReadStream(stream, maxResponseBytes);
        var payload = await JsonSerializer.DeserializeAsync<VaultSecretResponse>(
                limited,
                cancellationToken: timeout.Token)
            .ConfigureAwait(false);
        return payload?.Value
            ?? throw new InvalidOperationException("The external secret provider returned an invalid response.");
    }

    private sealed record VaultSecretResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("value")] string Value);
}
