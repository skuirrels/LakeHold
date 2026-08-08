using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Lakehold.Api.Mcp;

/// <summary>Registration and mapping for the Model Context Protocol endpoint.</summary>
public static class McpExtensions
{
    internal const string OperatorCommandMetadata = "lakehold/operator-command";

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
            .WithTools<LakeholdInspectionTools>()
            .WithTools<LakeholdSavedQueryTools>()
            .WithTools<LakeholdMaintenanceTools>()
            .WithTools<LakeholdWriteTools>()
            .WithTools<LakeholdConnectorTools>()
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

                    if (!settings.AllowOperatorCommands)
                    {
                        foreach (var operatorCommand in result.Tools
                                     .Where(tool => IsOperatorCommand(tool.Meta))
                                     .ToArray())
                        {
                            result.Tools.Remove(operatorCommand);
                        }
                    }

                    if (!settings.AllowWrites)
                    {
                        // Gate on the tool's own read-only annotation rather than a list of names.
                        // A name list silently stops covering the next mutating tool somebody adds,
                        // which is exactly how `execute` ended up being the only gated one.
                        foreach (var mutating in result.Tools
                                     .Where(tool => tool.Annotations?.ReadOnlyHint is not true)
                                     .ToArray())
                        {
                            result.Tools.Remove(mutating);
                        }
                    }

                    // A save must be visible on the next discovery request, including to clients
                    // that honour the protocol cache hint.
                    result.TimeToLive = TimeSpan.Zero;
                    return result;
                });

                filters.AddCallToolFilter(next => async (context, cancellationToken) =>
                {
                    var services = context.Services
                        ?? throw new McpException("No request services are available.");

                    // Removing a tool from discovery is not enforcement: a client with a stale tool
                    // cache calls it by name. Resolve the same annotation the list filter reads, so
                    // discovery and enforcement cannot describe different surfaces.
                    var tools = services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection;
                    if (tools is not null && tools.TryGetPrimitive(context.Params.Name, out var tool))
                    {
                        var settings = await services
                            .GetRequiredService<McpRuntimeSettingsStore>()
                            .GetAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (IsOperatorCommand(tool.ProtocolTool.Meta) && !settings.AllowOperatorCommands)
                        {
                            throw new McpException(
                                "Operator commands are disabled in LakeHold System Settings.");
                        }

                        if (tool.ProtocolTool.Annotations?.ReadOnlyHint is not true && !settings.AllowWrites)
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

    private static bool IsOperatorCommand(System.Text.Json.Nodes.JsonObject? metadata) =>
        metadata is not null
        && metadata.TryGetPropertyValue(OperatorCommandMetadata, out var value)
        && value is not null
        && value.GetValue<bool>();

    /// <summary>Maps the MCP endpoint; the request filter applies the current enabled setting.</summary>
    /// <remarks>
    ///     <see cref="McpAuthenticationFilter"/> guards it rather than
    ///     <c>LakeholdAuthorizationFilter</c>: this surface requires a named credential and never
    ///     accepts the demo reader that publishes a catalog anonymously (invariant 21).
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
