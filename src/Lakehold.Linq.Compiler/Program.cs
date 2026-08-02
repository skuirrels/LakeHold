using Microsoft.Extensions.Options;
using Lakehold.Linq.Compiler;
using Lakehold.Querying;
using System.Threading.RateLimiting;
using System.Security.Cryptography;
using System.Text;

if (args is ["--worker"])
{
    await LinqCompilerWorker.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), CancellationToken.None);
    return;
}

var builder = WebApplication.CreateBuilder(args);
var configuredLimits = builder.Configuration.GetSection(LinqCompilerOptions.Section).Get<LinqCompilerOptions>()
    ?? new LinqCompilerOptions();
builder.WebHost.ConfigureKestrel(server =>
    server.Limits.MaxRequestBodySize = configuredLimits.MaxRequestBodyBytes);
builder.Services.AddOptions<LinqCompilerOptions>()
    .Bind(builder.Configuration.GetSection(LinqCompilerOptions.Section))
    .Validate(
        options => options.MaxSourceLength > 0
            && options.MaxTables > 0
            && options.MaxColumns > 0
            && options.MaxArrayElements > 0
            && options.Timeout > TimeSpan.Zero
            && options.MaxConcurrentCompilations > 0
            && options.MaxQueuedCompilations >= 0
            && options.MaxRequestBodyBytes > 0,
        "LINQ compiler resource limits must be positive.")
    .ValidateOnStart();
builder.Services.AddSingleton<LinqQueryCompiler>();
builder.Services.AddSingleton<LinqCompilerProcess>();
builder.Services.AddRateLimiter(rateLimiter =>
{
    rateLimiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rateLimiter.AddPolicy("compilation", _ => RateLimitPartition.GetConcurrencyLimiter(
        "compiler",
        _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = configuredLimits.MaxConcurrentCompilations,
            QueueLimit = configuredLimits.MaxQueuedCompilations,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        }));
});

var app = builder.Build();
app.UseRateLimiter();
var configured = app.Services.GetRequiredService<IOptions<LinqCompilerOptions>>().Value;
if (app.Environment.IsProduction() && string.IsNullOrWhiteSpace(configured.SharedSecret))
{
    throw new InvalidOperationException(
        "Lakehold:LinqCompiler:SharedSecret is required when the LINQ compiler runs in production.");
}

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/health" || context.Request.Path == "/ready")
    {
        await next(context).ConfigureAwait(false);
        return;
    }

    var options = context.RequestServices.GetRequiredService<IOptions<LinqCompilerOptions>>().Value;
    if (!string.IsNullOrEmpty(options.SharedSecret)
        && !SecretsMatch(context.Request.Headers["X-Lakehold-Planner-Key"].ToString(), options.SharedSecret))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next(context).ConfigureAwait(false);
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/ready", async (LinqCompilerProcess compiler, CancellationToken cancellationToken) =>
{
    try
    {
        _ = await compiler.CompileAsync(new QueryPlanningRequest(
            "Main.Readiness.Take(1)",
            "readiness",
            [new QueryTableSchema(
                "main",
                "readiness",
                "TABLE",
                [new QueryColumnSchema("value", "INTEGER", false)])]), cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { status = "ready" });
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        return Results.Problem("LINQ compiler readiness failed.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});
app.MapGet("/descriptor", () => Results.Ok(new QueryLanguageDescriptor(
    "csharp-linq",
    "C# LINQ",
    "csharp",
    """
    from row in Main.Events
    select row
    """,
    ReadOnly: true,
    SupportsSavedQueries: true)));
app.MapPost("/starter", (
    QueryCatalogSchema request,
    LinqQueryCompiler compiler) =>
{
    try
    {
        return Results.Ok(compiler.CreateStarter(request));
    }
    catch (LinqPlanningException ex)
    {
        return Results.BadRequest(new QueryPlanningFailure(ex.Diagnostics));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new QueryPlanningFailure([
            new QueryDiagnostic("error", "LINQ000", ex.Message, 1, 1, 1, 1),
        ]));
    }
});
app.MapPost("/plan", async (
    QueryPlanningRequest request,
    LinqCompilerProcess compiler,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await compiler.CompileAsync(request, cancellationToken).ConfigureAwait(false));
    }
    catch (LinqPlanningException ex)
    {
        return Results.BadRequest(new QueryPlanningFailure(ex.Diagnostics));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new QueryPlanningFailure([
            new QueryDiagnostic("error", "LINQ000", ex.Message, 1, 1, 1, 1),
        ]));
    }
    catch (TimeoutException)
    {
        return Results.Problem("LINQ compilation exceeded the configured timeout.", statusCode: StatusCodes.Status408RequestTimeout);
    }
})
.RequireRateLimiting("compilation");

app.Run();

static bool SecretsMatch(string supplied, string configured)
{
    var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
    var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
    return CryptographicOperations.FixedTimeEquals(suppliedHash, configuredHash);
}

public partial class Program;
