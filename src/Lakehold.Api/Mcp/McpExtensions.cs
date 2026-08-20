using System.Diagnostics;
using Lakehold.Engine.Telemetry;
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
        builder.Services.AddLakeholdMcpRateLimiter();

        // Tools read the resolved principal off the request, which is how a tool learns who is calling
        // without the protocol carrying identity itself.
        builder.Services.AddHttpContextAccessor();

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "lakehold",
                    Title = "LakeHold",
                    Version = McpServerDescription.Version,
                };

                // Cross-cutting context that belongs to no single tool. Anything that fits in one
                // tool's description stays there; see the remarks on McpServerDescription.
                options.ServerInstructions = McpServerDescription.Instructions;

                // Completion for the resource templates' arguments. The templates disclose nothing by
                // being listed, which is the point of their being templates; this is the supported way
                // for a client to discover the values, and it answers per credential.
                options.Handlers.CompleteHandler = LakeholdCompletions.CompleteAsync;
            })
            .WithHttpTransport()
            .WithTools<LakeholdTools>()
            .WithTools<LakeholdQueryLanguageTools>()
            .WithTools<LakeholdInspectionTools>()
            .WithTools<LakeholdSavedQueryTools>()
            .WithTools<LakeholdMaintenanceTools>()
            .WithTools<LakeholdRestoreTools>()
            .WithTools<LakeholdWriteTools>()
            .WithTools<LakeholdConnectorTools>()
            .WithResources<LakeholdResources>()
            .WithRequestFilters(filters =>
            {
                filters.AddListToolsFilter(next => async (context, cancellationToken) =>
                {
                    var result = await next(context, cancellationToken).ConfigureAwait(false);
                    var settings = await SettingsAsync(context.Services, cancellationToken).ConfigureAwait(false);

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
                    var toolName = context.Params.Name;

                    // One span per tool call. Without it every agent request is an indistinguishable
                    // `POST /mcp` in the trace, and the engine spans underneath have no parent naming
                    // what asked for them.
                    using var activity = LakeholdTelemetry.Source.StartActivity("lakehold.mcp.tool");
                    activity?.SetTag(LakeholdTelemetry.ToolKey, toolName);
                    var startedAt = TimeProvider.System.GetTimestamp();

                    var mutating = IsMutating(context, toolName);

                    try
                    {
                        await EnsureToolIsEnabledAsync(context, toolName, cancellationToken).ConfigureAwait(false);
                        var result = await next(context, cancellationToken).ConfigureAwait(false);

                        // The SDK reports a tool failure as a result with IsError set rather than by
                        // throwing, so an outcome read from the exception alone would call every
                        // refusal a success.
                        var outcome = result.IsError is true
                            ? LakeholdTelemetry.OutcomeError
                            : LakeholdTelemetry.OutcomeSuccess;
                        Record(startedAt, toolName, outcome);
                        activity?.SetTag(LakeholdTelemetry.OutcomeKey, outcome);
                        if (mutating)
                        {
                            McpAudit.RecordMutation(context.Services!, context, toolName, outcome);
                        }

                        return result;
                    }
                    catch (Exception exception)
                    {
                        Record(startedAt, toolName, LakeholdTelemetry.OutcomeError);
                        activity?.SetTag(LakeholdTelemetry.OutcomeKey, LakeholdTelemetry.OutcomeError);
                        activity?.AddException(exception);
                        activity?.SetStatus(ActivityStatusCode.Error);
                        if (mutating)
                        {
                            // A refused mutation is worth a record too: a run of them is an agent
                            // repeatedly reaching for something it may not have.
                            McpAudit.RecordMutation(
                                context.Services!, context, toolName, LakeholdTelemetry.OutcomeError);
                        }

                        throw;
                    }
                });
            });

        return builder;
    }

    /// <summary>
    ///     Refuses a call to a tool the current runtime settings do not serve.
    /// </summary>
    /// <remarks>
    ///     Removing a tool from discovery is not enforcement: a client with a stale tool cache calls
    ///     it by name. Resolve the same annotation the list filter reads, so discovery and enforcement
    ///     cannot describe different surfaces.
    /// </remarks>
    private static async ValueTask EnsureToolIsEnabledAsync(
        RequestContext<CallToolRequestParams> context,
        string toolName,
        CancellationToken cancellationToken)
    {
        var tools = context.Services!.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection;
        if (tools is null || !tools.TryGetPrimitive(toolName, out var tool))
        {
            return;
        }

        var settings = await SettingsAsync(context.Services, cancellationToken).ConfigureAwait(false);
        if (IsOperatorCommand(tool.ProtocolTool.Meta) && !settings.AllowOperatorCommands)
        {
            LakeholdTelemetry.McpToolsGated.Add(1, Tool(toolName));
            throw new McpException("Operator commands are disabled in LakeHold System Settings.");
        }

        if (tool.ProtocolTool.Annotations?.ReadOnlyHint is not true && !settings.AllowWrites)
        {
            LakeholdTelemetry.McpToolsGated.Add(1, Tool(toolName));
            throw new McpException("Writing through MCP is disabled in LakeHold System Settings.");
        }
    }

    /// <summary>
    ///     Whether a tool changes anything, read from the same annotation the gates read.
    /// </summary>
    /// <remarks>
    ///     An unknown name is treated as non-mutating: it cannot run, so the SDK will refuse it, and
    ///     auditing a call that never reached a tool would fill the record with noise.
    /// </remarks>
    private static bool IsMutating(RequestContext<CallToolRequestParams> context, string toolName)
    {
        var tools = context.Services?.GetService<IOptions<McpServerOptions>>()?.Value.ToolCollection;
        return tools is not null
            && tools.TryGetPrimitive(toolName, out var tool)
            && tool.ProtocolTool.Annotations?.ReadOnlyHint is not true;
    }

    private static ValueTask<McpRuntimeSettings> SettingsAsync(
        IServiceProvider? services,
        CancellationToken cancellationToken)
        => McpCaller.SettingsAsync(
            services ?? throw new McpException("No request services are available."),
            cancellationToken);

    private static void Record(long startedAt, string tool, string outcome)
    {
        LakeholdTelemetry.McpToolCalls.Add(
            1,
            Tool(tool),
            new KeyValuePair<string, object?>(LakeholdTelemetry.OutcomeKey, outcome));
        LakeholdTelemetry.McpToolDuration.Record(
            TimeProvider.System.GetElapsedTime(startedAt).TotalSeconds,
            Tool(tool),
            new KeyValuePair<string, object?>(LakeholdTelemetry.OutcomeKey, outcome));
    }

    private static KeyValuePair<string, object?> Tool(string tool)
        => new(LakeholdTelemetry.ToolKey, tool);

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

        // Ahead of the authentication filter deliberately: a caller hammering the endpoint with a
        // bad credential should be shed before it costs a token lookup per request.
        group.RequireRateLimiting(McpRateLimiter.PolicyName);
        group.AddEndpointFilter<McpAuthenticationFilter>();
        group.MapMcp(string.Empty);

        return app;
    }
}
