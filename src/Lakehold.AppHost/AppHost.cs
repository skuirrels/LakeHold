// Lakehold local orchestration: the API plus the Angular dev server, wired together so a single
// `aspire run` brings up the whole product with logs and tracing in one dashboard.

var builder = DistributedApplication.CreateBuilder(args);

var api = builder
    .AddProject<Projects.Lakehold_Api>("api")
    .WithExternalHttpEndpoints();

// Angular 22's dev server runs on Vite. The UI reaches the API through the dev-server proxy
// (proxy.conf.json), so it needs the resolved API address rather than a hard-coded port.
builder
    .AddViteApp("ui", "../../web/lakehold-ui")
    .WithReference(api)
    .WaitFor(api)
    .WithEnvironment("NG_API_URL", api.GetEndpoint("http"))
    .WithExternalHttpEndpoints();

builder.Build().Run();
