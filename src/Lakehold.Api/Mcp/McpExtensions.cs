using Microsoft.Extensions.Options;

namespace Lakehold.Api.Mcp;

/// <summary>Registration and mapping for the Model Context Protocol endpoint.</summary>
public static class McpExtensions
{
    /// <summary>Registers the MCP server and its tools when <c>Lakehold:Mcp:Enabled</c> is set.</summary>
    public static IHostApplicationBuilder AddLakeholdMcp(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.Configure<McpOptions>(builder.Configuration.GetSection(McpOptions.SectionName));
        var options = builder.Configuration.GetSection(McpOptions.SectionName).Get<McpOptions>() ?? new McpOptions();
        if (!options.Enabled)
        {
            return builder;
        }

        // Tools read the resolved principal off the request, which is how a tool learns who is calling
        // without the protocol carrying identity itself.
        builder.Services.AddHttpContextAccessor();

        var server = builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<LakeholdTools>()
            .WithResources<LakeholdResources>();

        // Registered rather than mode-switched, so the annotations a client reads stay true and the
        // tool list says whether this deployment permits writes. See LakeholdWriteTools.
        if (options.AllowWrites)
        {
            server.WithTools<LakeholdWriteTools>();
        }

        return builder;
    }

    /// <summary>Maps the MCP endpoint when it is enabled.</summary>
    /// <remarks>
    ///     <see cref="McpAuthenticationFilter"/> guards it rather than
    ///     <c>LakeholdAuthorizationFilter</c>: this surface requires a credential unconditionally, and
    ///     never falls back to trusting the route (invariant 21).
    /// </remarks>
    public static IEndpointRouteBuilder MapLakeholdMcp(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.ServiceProvider.GetRequiredService<IOptions<McpOptions>>().Value;
        if (!options.Enabled)
        {
            return app;
        }

        // Mapped inside a group so the filter covers every endpoint the transport creates. MapMcp
        // returns an IEndpointConventionBuilder, which carries no endpoint filters of its own; a
        // group applies one to all of them, and to any the SDK adds in a later version.
        var group = app.MapGroup(options.Route);
        group.AddEndpointFilter<McpAuthenticationFilter>();
        group.MapMcp(string.Empty);

        return app;
    }
}
