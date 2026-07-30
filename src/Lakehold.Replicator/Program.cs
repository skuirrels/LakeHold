using System.Text.Json;
using System.Text.Json.Serialization;
using Lakehold.Client;
using Lakehold.Replication;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: Lakehold.Replicator <replica-config.json>");
    return 2;
}

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
json.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
var configuration = JsonSerializer.Deserialize<ReplicatorConfiguration>(
    await File.ReadAllTextAsync(args[0]).ConfigureAwait(false),
    json)
    ?? throw new InvalidOperationException("The replica configuration file is empty.");
configuration.Validate();

var token = Environment.GetEnvironmentVariable(configuration.TokenEnvironmentVariable);
if (string.IsNullOrWhiteSpace(token))
{
    throw new InvalidOperationException(
        $"Environment variable '{configuration.TokenEnvironmentVariable}' does not contain a LakeHold token.");
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

using var http = new HttpClient
{
    BaseAddress = new Uri(configuration.SourceUrl, UriKind.Absolute),
    Timeout = TimeSpan.FromMinutes(2),
};
var client = new LakeholdClient(http, token);
var target = new DuckDbReplica(configuration.TargetPath);
var source = new ReplicaSource(
    configuration.SourceId,
    configuration.Tenant,
    configuration.Catalog,
    configuration.Tables,
    configuration.PageSize);
var replicator = new LakeholdReplicator(client, target, source);

do
{
    var checkpoint = await replicator.ReplicateOnceAsync(cancellation.Token).ConfigureAwait(false);
    Console.WriteLine(
        $"{DateTimeOffset.UtcNow:O} source={checkpoint.SourceId} snapshot={checkpoint.LastAppliedSnapshot}");

    if (configuration.RunOnce)
    {
        break;
    }

    await Task.Delay(configuration.PollInterval, cancellation.Token).ConfigureAwait(false);
}
while (!cancellation.IsCancellationRequested);

return 0;

internal sealed record ReplicatorConfiguration
{
    public required string SourceUrl { get; init; }

    public required string TokenEnvironmentVariable { get; init; }

    public required string SourceId { get; init; }

    public required string Tenant { get; init; }

    public required string Catalog { get; init; }

    public required string TargetPath { get; init; }

    public required IReadOnlyList<ReplicaTableSelection> Tables { get; init; }

    public int PageSize { get; init; } = 5_000;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(15);

    public bool RunOnce { get; init; }

    public void Validate()
    {
        _ = new Uri(SourceUrl, UriKind.Absolute);
        ArgumentException.ThrowIfNullOrWhiteSpace(TokenEnvironmentVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(Catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(TargetPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PageSize);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PollInterval, TimeSpan.Zero);
        if (Tables.Count == 0)
        {
            throw new InvalidOperationException("At least one table must be configured.");
        }
    }
}
