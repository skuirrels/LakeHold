using DuckDB.EFCoreProvider.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lakehold.Api;
using Lakehold.Api.Auth;
using Lakehold.Api.Cdc;
using Lakehold.Api.Endpoints;
using Lakehold.Api.Health;
using Lakehold.Api.PgWire;
using Lakehold.Api.Scheduling;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Security;
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
    options.MetadataRoot = Path.GetFullPath(Path.Combine(stateRoot, "catalogs"));
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

// Authentication: resolve a bearer token to a principal, then validate the route against it in the
// endpoint filter. Off by default for token-less requests until issuance and the UI wiring land —
// see LakeholdAuthOptions and docs/AUTHENTICATION.md.
builder.Services.Configure<LakeholdAuthOptions>(builder.Configuration.GetSection(LakeholdAuthOptions.Section));
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddScoped<ApiTokenAuthenticator>();

// OIDC for humans, tokens for machines, one principal behind both. Configuring an authority is what
// turns this on: absent one the whole path stays off, so an air-gapped install never acquires a
// dependency on an identity provider it cannot reach. See docs/AUTHENTICATION.md.
builder.Services.Configure<LakeholdOidcOptions>(builder.Configuration.GetSection(LakeholdOidcOptions.Section));
var oidc = builder.Configuration.GetSection(LakeholdOidcOptions.Section).Get<LakeholdOidcOptions>() ?? new LakeholdOidcOptions();
if (oidc.Enabled)
{
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = oidc.Authority;
            options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;

            // An unset audience validates the issuer and signature but not the audience. That is a
            // deliberate choice left to the operator, not a default: some IdPs do not issue an
            // audience a resource server can match, and refusing to start would be worse than
            // documenting the narrower guarantee.
            if (oidc.Audience.Length > 0)
            {
                options.Audience = oidc.Audience;
            }
            else
            {
                options.TokenValidationParameters.ValidateAudience = false;
            }
        });

    builder.Services.AddAuthorization();
}

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
    if (pgWire.Password.Length == 0 && pgWire.TenantPasswords.Count == 0
        && !pgWire.AllowAnonymous && !pgWire.AllowTokenAuthentication)
    {
        throw new InvalidOperationException(
            "Lakehold:PgWire is enabled but no credentials are configured. Set per-tenant passwords "
            + "(Lakehold__PgWire__TenantPasswords__<tenant>) in .env, or Lakehold__PgWire__Password "
            + "for a single shared credential, or set Lakehold:PgWire:AllowTokenAuthentication to "
            + "accept API tokens, or set Lakehold:PgWire:AllowAnonymous to true to accept "
            + "unauthenticated connections deliberately.");
    }

    // Token authentication has to ask for the password in the clear, because the token store holds
    // only a hash. Refusing to start is the right failure: the alternative is a credential that
    // authenticates every Lakehold surface crossing an unencrypted socket.
    if (pgWire.AllowTokenAuthentication && !pgWire.RequireTls && !pgWire.AllowCleartextPassword)
    {
        throw new InvalidOperationException(
            "Lakehold:PgWire:AllowTokenAuthentication requires the password in the clear, so it must "
            + "run under TLS. Set Lakehold:PgWire:RequireTls (with a certificate), or set "
            + "Lakehold:PgWire:AllowCleartextPassword to accept the risk on a trusted network.");
    }

    if (pgWire.RequireTls && pgWire.TlsCertificatePath.Length == 0)
    {
        // Refusing every connection at run time is a worse way to learn this than refusing to start.
        throw new InvalidOperationException(
            "Lakehold:PgWire:RequireTls is set but no certificate is configured. Set "
            + "Lakehold:PgWire:TlsCertificatePath, or clear RequireTls to serve plaintext.");
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

// Only when an authority is configured: without a registered scheme these throw, and the endpoint
// filter is what enforces access in either case — this middleware only populates HttpContext.User.
if (oidc.Enabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapLakehouseEndpoints();

app.LogMaintenanceSchedule();

// Schema initialisation always runs; the demo catalog only where it was asked for. Defaulting to
// the environment rather than to true means a production image seeds nothing unless told to, and a
// developer's compose stack is still self-demonstrating on first run.
var seedDemoData = builder.Configuration.GetValue("Lakehold:SeedDemoData", app.Environment.IsDevelopment());
await DemoData.EnsureSeededAsync(app.Services, stateRoot, app.Logger, seedDemoData).ConfigureAwait(false);

// Bootstrap the first credential once the schema exists. On a node with no tokens this mints an
// instance-scoped one and logs it once, so a fresh production deployment can be provisioned at all.
// Lakehold__BootstrapToken overrides it for platforms that inject credentials externally.
await TokenBootstrap.EnsureBootstrapTokenAsync(
    app.Services,
    builder.Configuration["Lakehold:BootstrapToken"],
    app.Logger,
    TimeProvider.System).ConfigureAwait(false);

await app.RunAsync().ConfigureAwait(false);
