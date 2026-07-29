using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Lakehold.Api.Mcp;

/// <summary>Registration and mapping for the Model Context Protocol endpoint.</summary>
public static class McpExtensions
{
    /// <summary>Registers the MCP server and its runtime-filtered tools.</summary>
    public static IHostApplicationBuilder AddLakeholdMcp(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.Configure<McpOptions>(builder.Configuration.GetSection(McpOptions.SectionName));
        builder.Services.TryAddScoped<McpRuntimeSettingsStore>();

        // Tools read the resolved principal off the request, which is how a tool learns who is calling
        // without the protocol carrying identity itself.
        builder.Services.AddHttpContextAccessor();

        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<LakeholdTools>()
            .WithTools<LakeholdWriteTools>()
            .WithResources<LakeholdResources>()
            .WithRequestFilters(filters =>
            {
                filters.AddListToolsFilter(next => async (context, cancellationToken) =>
                {
                    var result = await next(context, cancellationToken).ConfigureAwait(false);
                    var services = context.Services
                        ?? throw new McpException("No request services are available.");
                    var settings = await services
                        .GetRequiredService<McpRuntimeSettingsStore>()
                        .GetAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (!settings.AllowWrites)
                    {
                        var execute = result.Tools.FirstOrDefault(
                            tool => string.Equals(tool.Name, "execute", StringComparison.Ordinal));
                        if (execute is not null)
                        {
                            result.Tools.Remove(execute);
                        }
                    }

                    // A save must be visible on the next discovery request, including to clients
                    // that honour the protocol cache hint.
                    result.TimeToLive = TimeSpan.Zero;
                    return result;
                });

                filters.AddCallToolFilter(next => async (context, cancellationToken) =>
                {
                    if (string.Equals(context.Params.Name, "execute", StringComparison.Ordinal))
                    {
                        var services = context.Services
                            ?? throw new McpException("No request services are available.");
                        var settings = await services
                            .GetRequiredService<McpRuntimeSettingsStore>()
                            .GetAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (!settings.AllowWrites)
                        {
                            throw new McpException(
                                "Writing through MCP is disabled in LakeHold System Settings.");
                        }
                    }

                    return await next(context, cancellationToken).ConfigureAwait(false);
                });
            });

        return builder;
    }

    /// <summary>Maps the MCP endpoint; the request filter applies the current enabled setting.</summary>
    /// <remarks>
    ///     <see cref="McpAuthenticationFilter"/> guards it rather than
    ///     <c>LakeholdAuthorizationFilter</c>: this surface requires a credential unconditionally, and
    ///     never falls back to trusting the route (invariant 21).
    /// </remarks>
    public static IEndpointRouteBuilder MapLakeholdMcp(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.ServiceProvider.GetRequiredService<IOptions<McpOptions>>().Value;
        // Mapped inside a group so the filter covers every endpoint the transport creates. MapMcp
        // returns an IEndpointConventionBuilder, which carries no endpoint filters of its own; a
        // group applies one to all of them, and to any the SDK adds in a later version.
        var group = app.MapGroup(options.Route);
        group.AddEndpointFilter<McpAuthenticationFilter>();
        group.MapMcp(string.Empty);

        return app;
    }
}
