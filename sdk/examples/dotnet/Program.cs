using Lakehold.Sdk.Runtime;

static string Required(string name) => Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"{name} is required.");

using var client = LakeholdRuntime.Configure(new HttpClient(), TimeSpan.FromSeconds(30));
await foreach (var item in LakeholdRuntime.StreamQueryAsync(
                   client,
                   new Uri(Required("LAKEHOLD_URL").TrimEnd('/') + "/"),
                   Required("LAKEHOLD_TOKEN"),
                   Required("LAKEHOLD_TENANT"),
                   Required("LAKEHOLD_CATALOG"),
                   "SELECT 1 AS value"))
{
    Console.WriteLine(item.Payload.GetRawText());
}
