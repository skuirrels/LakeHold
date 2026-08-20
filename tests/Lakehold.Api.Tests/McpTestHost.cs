using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api.Auth;
using Lakehold.Api.Connectors;
using Lakehold.Api.Mcp;
using Lakehold.Api.Querying;
using Lakehold.Api.Security;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Security;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Lakehold.Querying;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Lakehold.Api.Tests;

/// <summary>
///     The service registrations an MCP test host needs, in one place.
/// </summary>
/// <remarks>
///     <para>
///         This exists because of a real gap it now closes. Both MCP test hosts registered
///         <c>LakehouseService</c> and nothing else, so the saved-query, connector, and query-language
///         tools could not be <em>constructed</em> in a test at all — a call to one failed in the DI
///         container before it reached any code worth asserting on. The exposed-tool-set assertion
///         still passed, because listing tools only reflects attributes, which is exactly how twenty
///         tools came to be covered by their name in a list and by nothing else.
///     </para>
///     <para>
///         Keeping the registrations here rather than copied into each fixture means a tool whose
///         dependency is missing fails <em>every</em> MCP suite at once, loudly, instead of silently
///         narrowing what the suites are able to exercise.
///     </para>
/// </remarks>
internal static class McpTestHost
{
    /// <summary>The one host a connector definition may name in a test.</summary>
    public const string AllowedConnectorHost = "connector.test";

    /// <summary>Registers everything the MCP tool surface resolves, then the MCP server itself.</summary>
    public static void AddLakeholdMcpForTests(this WebApplicationBuilder builder, string root)
    {
        builder.Services.AddDbContext<ControlPlaneContext>(
            o => o.UseDuckDB($"Data Source={Path.Combine(root, "cp.duckdb")}"));
        builder.Services.AddScoped<ApiTokenAuthenticator>();
        builder.Services.AddScoped<MemberDirectory>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.Configure<LakehouseOptions>(o =>
        {
            o.MetadataRoot = Path.Combine(root, "catalogs");
            o.DataRoot = Path.Combine(root, "data");
        });
        builder.Services.AddSingleton<DucklingPool>();
        builder.Services.AddSingleton<CatalogCache>();
        builder.Services.AddScoped<LakehouseService>();

        // Query planning. The real planner is an out-of-process compiler; the fake keeps SQL working
        // exactly as it does in production and gives the language tools something honest to report.
        builder.Services.AddSingleton<StubQuerySourcePlanner>();
        builder.Services.AddScoped<IQuerySourcePlanner>(
            services => services.GetRequiredService<StubQuerySourcePlanner>());
        builder.Services.AddScoped<QuerySourcePlanningService>();
        builder.Services.AddScoped<QueryExecutionCoordinator>();
        builder.Services.AddScoped<SavedQueryService>();

        // Managed connectors. The REST adapter is registered because validation resolves a
        // definition's adapter manifest — without one, every connector definition is refused as
        // naming an unregistered adapter, and the connector tools could not be exercised at all.
        // ConnectorExecutionService resolves its runner lazily, so no runner is needed to cover
        // everything except an actual refresh.
        builder.Services.AddHttpClient(RestDataConnectorSource.HttpClientName);
        builder.Services.AddScoped<IConnectorSecretProvider, EnvironmentConnectorSecretProvider>();
        builder.Services.AddScoped<ConnectorSecretResolver>();
        builder.Services.AddScoped<IDataConnectorSource, RestDataConnectorSource>();
        builder.Services.AddScoped<DataConnectorService>();
        builder.Services.AddScoped<DataConnectorSourceResolver>();
        builder.Services.AddScoped<ConnectorExecutionService>();
        builder.Services.Configure<ConnectorOptions>(o =>
        {
            // One named host, and no DNS. The egress policy is the connector subsystem's to test;
            // here it only needs to admit a definition so the *MCP* tools around it can be exercised
            // offline. Naming a host rather than allowing everything keeps the refusal path testable.
            o.AllowedHosts = [AllowedConnectorHost];
            o.AllowUnsafeDestinations = true;
        });

        builder.AddLakeholdMcp();
    }
}

/// <summary>
///     A planner that reports SQL and one deliberately unavailable language.
/// </summary>
/// <remarks>
///     The unavailable entry is the interesting one: it is what proves <c>list_query_languages</c>
///     reports a language its compiler cannot reach, with a reason, rather than omitting it — which
///     would leave an agent to conclude the language never existed and to keep guessing at ids.
/// </remarks>
internal sealed class StubQuerySourcePlanner : IQuerySourcePlanner
{
    /// <summary>Language id the stub can actually plan, by compiling it to its own body.</summary>
    public const string PlannableLanguage = "echo";

    /// <summary>Language id the stub advertises but refuses, as an unreachable compiler would.</summary>
    public const string UnavailableLanguage = "linq";

    public Task<IReadOnlyList<QueryLanguageDescriptor>> GetLanguagesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<QueryLanguageDescriptor>>(
        [
            new QueryLanguageDescriptor("sql", "SQL", "sql", "SELECT 1", ReadOnly: false, SupportsSavedQueries: true),
            new QueryLanguageDescriptor(
                PlannableLanguage, "Echo", "sql", "SELECT 1", ReadOnly: true, SupportsSavedQueries: true),
            new QueryLanguageDescriptor(
                UnavailableLanguage,
                "LINQ",
                "csharp",
                "from x in table select x",
                ReadOnly: true,
                SupportsSavedQueries: true,
                Available: false,
                UnavailableReason: "The LINQ compiler is not reachable."),
        ]);

    public Task<QueryLanguageStarter> CreateStarterAsync(
        string language,
        QueryCatalogSchema catalogSchema,
        CancellationToken cancellationToken)
        => Task.FromResult(new QueryLanguageStarter("SELECT 1", "fingerprint"));

    public Task<QueryPlan> PlanAsync(
        string language,
        QueryPlanningRequest planningRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(planningRequest);

        if (language == UnavailableLanguage)
        {
            throw new QueryLanguageUnavailableException(language);
        }

        // The stub compiles a source to itself, so a test can assert that what ran is what the plan
        // reported without needing a real compiler in the loop. The fingerprint is echoed back
        // because QueryPlanValidator rejects a plan whose fingerprint does not match the catalog
        // schema the request was built from — which is a real protection, not a test detail.
        return Task.FromResult(
            new QueryPlan(planningRequest.Source, [], [], planningRequest.SchemaFingerprint));
    }
}
