using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Lakehold.Client;

public sealed record LakeholdColumn(string Name, string DataType, string ClrType);

public sealed record LakeholdQueryPage(
    IReadOnlyList<LakeholdColumn> Columns,
    IReadOnlyList<JsonElement[]> Rows,
    bool Truncated,
    double ElapsedMilliseconds,
    long? RowsAffected);

public sealed record LakeholdSchemaColumn(string Name, string DataType, bool IsNullable);

public sealed record LakeholdSchemaTable(
    string Name,
    string Kind,
    IReadOnlyList<LakeholdSchemaColumn> Columns);

public sealed record LakeholdSchema(string Name, IReadOnlyList<LakeholdSchemaTable> Tables);

public sealed record LakeholdSnapshot(
    long SnapshotId,
    DateTimeOffset CommittedAt,
    long SchemaVersion,
    string? CommitMessage);

public sealed record LakeholdChange(
    long SnapshotId,
    long RowId,
    string ChangeType,
    IReadOnlyDictionary<string, JsonElement> Row);

public sealed record LakeholdChangePage(
    string Schema,
    string Table,
    long FromSnapshot,
    long ToSnapshot,
    bool Truncated,
    IReadOnlyList<LakeholdChange> Changes,
    string? NextCursor);

public sealed record LakeholdCdcConsumer(
    int Id,
    string Name,
    string Catalog,
    long LastAppliedSnapshot,
    bool Active,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>Authenticated client for the LakeHold surfaces required by replication.</summary>
public sealed class LakeholdClient(HttpClient httpClient, string token)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<LakeholdSnapshot?> GetLatestSnapshotAsync(
        string tenant,
        string catalog,
        CancellationToken cancellationToken)
    {
        var snapshots = await GetAsync<IReadOnlyList<LakeholdSnapshot>>(
                $"/api/tenants/{Escape(tenant)}/catalogs/{Escape(catalog)}/snapshots?limit=1",
                cancellationToken)
            .ConfigureAwait(false);
        return snapshots.Count == 0 ? null : snapshots[0];
    }

    public Task<IReadOnlyList<LakeholdSchema>> GetSchemasAsync(
        string tenant,
        string catalog,
        CancellationToken cancellationToken)
        => GetAsync<IReadOnlyList<LakeholdSchema>>(
            $"/api/tenants/{Escape(tenant)}/catalogs/{Escape(catalog)}/schemas",
            cancellationToken);

    public async Task<LakeholdQueryPage> ExecuteQueryAsync(
        string tenant,
        string catalog,
        string sql,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"/api/tenants/{Escape(tenant)}/catalogs/{Escape(catalog)}/query");
        request.Content = JsonContent.Create(new ExecuteRequest(sql), options: Json);

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<LakeholdQueryPage>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<LakeholdChangePage> GetChangesAsync(
        string tenant,
        string catalog,
        string schema,
        string table,
        long snapshot,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var path =
            $"/api/tenants/{Escape(tenant)}/catalogs/{Escape(catalog)}/cdc/snapshots/"
            + $"{snapshot.ToString(CultureInfo.InvariantCulture)}/changes"
            + $"?schema={Escape(schema)}&table={Escape(table)}&limit={Math.Min(limit, 10_000)}";
        if (!string.IsNullOrEmpty(cursor))
        {
            path += $"&cursor={Escape(cursor)}";
        }

        return GetAsync<LakeholdChangePage>(path, cancellationToken);
    }

    public async Task<LakeholdCdcConsumer> RegisterConsumerAsync(
        string tenant,
        string catalog,
        string name,
        long lastAppliedSnapshot,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"/api/tenants/{Escape(tenant)}/catalogs/{Escape(catalog)}/cdc/consumers");
        request.Content = JsonContent.Create(
            new RegisterConsumerRequest(name, lastAppliedSnapshot),
            options: Json);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<LakeholdCdcConsumer>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LakeholdCdcConsumer> AdvanceConsumerAsync(
        string tenant,
        string catalog,
        int consumerId,
        long lastAppliedSnapshot,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Put,
            $"/api/tenants/{Escape(tenant)}/catalogs/{Escape(catalog)}/cdc/consumers/{consumerId}/checkpoint");
        request.Content = JsonContent.Create(
            new AdvanceConsumerRequest(lastAppliedSnapshot),
            options: Json);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<LakeholdCdcConsumer>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new LakeholdClientException((int)response.StatusCode, detail);
        }

        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new LakeholdClientException((int)response.StatusCode, "LakeHold returned an empty JSON response.");
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private sealed record ExecuteRequest(string Sql);

    private sealed record RegisterConsumerRequest(string Name, long LastAppliedSnapshot);

    private sealed record AdvanceConsumerRequest(long LastAppliedSnapshot);
}

public sealed class LakeholdClientException(int statusCode, string detail)
    : Exception($"LakeHold returned HTTP {statusCode}: {detail}")
{
    public int StatusCode { get; } = statusCode;
}
