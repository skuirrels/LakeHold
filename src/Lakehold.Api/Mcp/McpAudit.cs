using System.Text.Json;
using Lakehold.Api.Auth;
using Lakehold.ControlPlane.Security;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Lakehold.Api.Mcp;

/// <summary>
///     A structured record of every MCP call that changes something, naming the actor.
/// </summary>
/// <remarks>
///     <para>
///         <c>QueryRun</c> already attributes statements — including those an agent runs through
///         <c>query</c>, <c>execute</c>, and the saved-query tools — with a token or member id and an
///         <c>Origin</c> of <c>Mcp</c>. What it does not cover is the mutating tools that are not
///         statements: retiring a connector, running one, applying maintenance, publishing a view.
///         Those changed durable state with no record of who asked, which is tolerable when a person
///         clicked a button in a UI that required them to sign in, and much less so when the caller is
///         an autonomous program.
///     </para>
///     <para>
///         So every non-read-only tool call is logged here with its actor, tool, target, and outcome.
///         <b>Arguments are not logged</b>, and that is a rule rather than an oversight: the mutating
///         set includes <c>execute</c> and <c>create_saved_query</c>, whose arguments are submitted
///         SQL, and connector tools whose definitions carry secret references. Tenant and catalog are
///         identifiers a log already carries elsewhere, so they are recorded and nothing else is.
///     </para>
///     <para>
///         This is a log, not a durable audit row. Connector and maintenance entities carry no actor
///         columns, and adding them is a control-plane schema change to those subsystems rather than
///         to this one; see the note in <c>docs/MCP.md</c>. The log closes the "who" question for an
///         operator reading an incident today without pretending to the retention guarantee
///         <c>QueryRun</c> gives.
///     </para>
/// </remarks>
internal static class McpAudit
{
    private static readonly Action<ILogger, string, string, string, string, string, Exception?> MutatingCall =
        LoggerMessage.Define<string, string, string, string, string>(
            LogLevel.Information,
            new EventId(1100, "McpMutatingToolCall"),
            "MCP mutating tool {Tool} called by {Actor} for tenant {Tenant} catalog {Catalog}: {Outcome}");

    /// <summary>Records a completed mutating call. Never records tool arguments.</summary>
    public static void RecordMutation(
        IServiceProvider services,
        RequestContext<CallToolRequestParams> context,
        string tool,
        string outcome)
    {
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("Lakehold.Api.Mcp.Audit");
        if (logger is null || !logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        MutatingCall(
            logger,
            tool,
            Actor(services),
            Argument(context, "tenant"),
            Argument(context, "catalog"),
            outcome,
            null);
    }

    /// <summary>
    ///     The calling credential as a stable, non-secret identifier.
    /// </summary>
    /// <remarks>
    ///     A token is named by its database id, never by its value or its prefix — the id is what an
    ///     operator looks up under Users, and it is what survives the token being revoked. A member is
    ///     named by member id for the same reason, and the two are kept distinguishable because
    ///     <c>QueryRun</c> keeps them mutually exclusive and this must agree with it.
    /// </remarks>
    private static string Actor(IServiceProvider services)
    {
        var http = services.GetService<IHttpContextAccessor>()?.HttpContext;
        if (http?.Items.TryGetValue(LakeholdAuthorizationFilter.PrincipalItemKey, out var value) is not true
            || value is not ILakeholdPrincipal principal)
        {
            return "unknown";
        }

        return principal.MemberId is { } member
            ? $"member:{member}"
            : principal.TokenId is { } token
                ? $"token:{token}"
                : "unattributed";
    }

    /// <summary>
    ///     One named argument, when it is one of the two identifiers this is allowed to record.
    /// </summary>
    private static string Argument(RequestContext<CallToolRequestParams> context, string name)
        => context.Params?.Arguments is { } arguments
            && arguments.TryGetValue(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "-"
                : "-";
}
