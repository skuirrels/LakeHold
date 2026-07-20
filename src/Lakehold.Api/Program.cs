using DuckDB.EFCoreProvider.Extensions;
using Microsoft.EntityFrameworkCore;
using Lakehold.Api;
using Lakehold.Api.Endpoints;
using Lakehold.Api.Scheduling;
using Lakehold.ControlPlane.Data;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

builder.Services.Configure<LakehouseOptions>(builder.Configuration.GetSection(LakehouseOptions.SectionName));

var stateRoot = Path.GetFullPath(builder.Configuration["Lakehouse:StateRoot"] ?? "./.lakehold");
Directory.CreateDirectory(stateRoot);

// Resolve storage roots against the state root so a relative default does not follow the process's
// working directory. BackupRoot is deliberately a sibling of the data root: nested under it, backups
// become candidates for DuckLake's own orphan cleanup and eventually delete themselves.
builder.Services.PostConfigure<LakehouseOptions>(options =>
{
    options.DataRoot = Path.GetFullPath(Path.Combine(stateRoot, "data"));
    options.BackupRoot = Path.GetFullPath(Path.Combine(stateRoot, "backups"));
});

// Control plane on a native DuckDB file: it needs sequences, RETURNING, and migrations, none of
// which the provider's DuckLake profile supports. See docs/ARCHITECTURE.md.
builder.Services.AddDbContext<ControlPlaneContext>(options =>
    options.UseDuckDB($"Data Source={Path.Combine(stateRoot, "controlplane.duckdb")}"));

// One pool per node — it owns the warm compute sessions, so it must outlive any request.
builder.Services.AddSingleton<DucklingPool>();
builder.Services.AddScoped<LakehouseService>();

// Scheduled flush/backup/compact. A backup that depends on someone pressing a button is not a
// recovery guarantee; unflushed inlined data is permanently unrecoverable, so both must be automatic.
builder.AddMaintenanceScheduling();

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
