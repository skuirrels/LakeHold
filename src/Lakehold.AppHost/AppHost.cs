// Lakehold local orchestration: the API plus the Angular dev server, wired together so a single
// `aspire run` brings up the whole product with logs and tracing in one dashboard.

var builder = DistributedApplication.CreateBuilder(args);

var api = builder
    .AddProject<Projects.Lakehold_Api>("api")
    .WithExternalHttpEndpoints();

// Angular 22's dev server runs on Vite. The UI reaches the API through the dev-server proxy
// (proxy.conf.json), which targets the API's launch-profile port — the same port Aspire's proxy
// publishes it on, so the two agree.
//
// The run script must be named explicitly. AddViteApp defaults to "dev", which this project does
// not define, and the failure is silent in the worst way: `npm run dev` exits immediately with
// "Missing script", while Aspire still reports the resource as Running and ready because a Vite app
// has no health check to contradict it. The dashboard showed a healthy UI that was never listening.
// The published port is pinned so `aspire run` serves the UI somewhere memorable and documented,
// rather than at a fresh random port every run that has to be looked up in the dashboard first.
// Only the proxy's port is fixed; the dev server behind it still binds wherever Aspire puts it.
// The API needs no equivalent — its launch profile already pins it to 5200, which is what
// proxy.conf.json targets.
builder
    .AddViteApp("ui", "../../web/lakehold-ui", "start")
    .WithReference(api)
    .WaitFor(api)
    .WithEnvironment("NG_API_URL", api.GetEndpoint("http"))
    .WithEndpoint("http", endpoint => endpoint.Port = 5399)
    .WithExternalHttpEndpoints();

builder.Build().Run();
