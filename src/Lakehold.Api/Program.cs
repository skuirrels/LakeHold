using DuckDB.EFCoreProvider.Extensions;
using Microsoft.EntityFrameworkCore;
using Lakehold.Api;
using Lakehold.Api.Cdc;
using Lakehold.Api.Endpoints;
using Lakehold.Api.Health;
using Lakehold.Api.PgWire;
using Lakehold.Api.Scheduling;
using Lakehold.ControlPlane.Data;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Lakehold.Engine.Telemetry;

// Load .env before the host is built, because the environment-variable configuration provider reads
// the process environment during CreateBuilder — set them afterwards and configuration never sees
// them. TraversePath walks up to the repository root, so this works whether the API is launched from
// its own directory, from the solution root, or by Aspire.
//
// Real environment variables win: Load does not overwrite what is already set, so a value exported
// by the shell, a container, or Aspire is never clobbered by a stale local file. Absent .env, this
// is a no-op — deployments set configuration through their platform, not through a file in source.
DotNetEnv.Env.TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Lakehold's own spans and instruments. AddServiceDefaults wires the HTTP and runtime
// instrumentation, which can say a request took 400 ms but not whether that was DuckDB executing, a
// cold session attaching, or the statement queued behind another on the same tenant's gate.
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(LakeholdTelemetry.ActivitySourceName))
    .WithMetrics(metrics => metrics.AddMeter(LakeholdTelemetry.MeterName));

// Readiness gains a real dependency. AddServiceDefaults registers only the "self" liveness check, so
// without this /health would report ready before the control plane was open.
builder.Services.AddHealthChecks()
    .AddCheck<ControlPlaneHealthCheck>("control-plane");

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

builder.Services.Configure<LakehouseOptions>(builder.Configuration.GetSection(LakehouseOptions.SectionName));

var stateRoot = Path.GetFullPath(builder.Configuration["Lakehouse:StateRoot"] ?? "./.lakehold");
Directory.CreateDirectory(stateRoot);

// Resolve storage roots against the state root so a relative default does not follow the process's
// working directory. BackupRoot and EjectRoot are deliberately siblings of the data root: nested
// under it, both become candidates for DuckLake's own orphan cleanup and eventually delete themselves.
builder.Services.PostConfigure<LakehouseOptions>(options =>
{
    options.DataRoot = Path.GetFullPath(Path.Combine(stateRoot, "data"));
    options.BackupRoot = Path.GetFullPath(Path.Combine(stateRoot, "backups"));
    options.EjectRoot = Path.GetFullPath(Path.Combine(stateRoot, "ejects"));
});

// Control plane on a native DuckDB file: it needs sequences, RETURNING, and migrations, none of
// which the provider's DuckLake profile supports. See docs/ARCHITECTURE.md.
builder.Services.AddDbContext<ControlPlaneContext>(options =>
    options.UseDuckDB($"Data Source={Path.Combine(stateRoot, "controlplane.duckdb")}"));

// One pool per node — it owns the warm compute sessions, so it must outlive any request. The
// catalog cache is a singleton for the same reason: it spares every query a control-plane read to
// re-resolve a catalog record that rarely changes.
builder.Services.AddSingleton<DucklingPool>();
builder.Services.AddSingleton<CatalogCache>();
builder.Services.AddScoped<LakehouseService>();

// Scheduled flush/backup/compact. A backup that depends on someone pressing a button is not a
// recovery guarantee; unflushed inlined data is permanently unrecoverable, so both must be automatic.
builder.AddMaintenanceScheduling();

// Outbound CDC: polls subscribed catalogs for new snapshots and posts signed change payloads.
// DuckLake already records what every snapshot changed, so this reads existing bookkeeping rather
// than adding a Debezium/Kafka pipeline beside the lakehouse.
builder.Services.Configure<CdcOptions>(builder.Configuration.GetSection(CdcOptions.SectionName));
builder.Services.AddHttpClient(ChangeFeedDispatcher.HttpClientName);
if (builder.Configuration.GetSection(CdcOptions.SectionName).Get<CdcOptions>()?.Enabled ?? true)
{
    builder.Services.AddHostedService<ChangeFeedDispatcher>();
}

// PostgreSQL wire endpoint: lets Power BI, Tableau, Metabase, and psql connect to a tenant catalog
// with no connector to install, because they already speak this protocol. See docs/POSTGRES-WIRE.md.
builder.Services.Configure<PgWireOptions>(builder.Configuration.GetSection(PgWireOptions.SectionName));
var pgWire = builder.Configuration.GetSection(PgWireOptions.SectionName).Get<PgWireOptions>() ?? new PgWireOptions();
if (pgWire.Enabled)
{
    // Fail closed. This opens a database port onto every catalog the node serves, so starting it
    // without a password has to be an explicit decision rather than the consequence of an
    // unset configuration key.
    if (pgWire.Password.Length == 0 && !pgWire.AllowAnonymous)
    {
        throw new InvalidOperationException(
            "Lakehold:PgWire is enabled but no password is configured. Set Lakehold__PgWire__Password "
            + "in .env, or set Lakehold:PgWire:AllowAnonymous to true to accept unauthenticated "
            + "connections deliberately.");
    }

    builder.Services.AddHostedService<PgWireServer>();
}

// The Angular dev server is a separate origin; the browser will not call the API without this.
const string DevCors = "lakehold-dev";
builder.Services.AddCors(options => options.AddPolicy(DevCors, policy => policy
    .WithOrigins("http://localhost:5399", "https://localhost:5399")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseCors(DevCors);
}

app.UseExceptionHandler();
app.MapLakehouseEndpoints();

app.LogMaintenanceSchedule();

await DemoData.EnsureSeededAsync(app.Services, stateRoot, app.Logger).ConfigureAwait(false);

await app.RunAsync().ConfigureAwait(false);
