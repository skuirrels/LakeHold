using System.Text.Json;
using DuckDB.NET.Data;
using Lakehold.Replication;
using Xunit;

namespace Lakehold.Replication.Tests;

public sealed class DuckDbReplicaTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "lakehold-replication-tests",
        Guid.NewGuid().ToString("N"));
    private string _targetPath = null!;
    private DuckDbReplica _replica = null!;

    private static readonly ReplicaTableDefinition Orders = new(
        "main",
        "orders",
        [
            new ReplicaColumn("id", "BIGINT", IsNullable: false),
            new ReplicaColumn("status", "VARCHAR", IsNullable: false),
        ],
        ReplicaTableMode.Keyed,
        ["id"]);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _targetPath = Path.Combine(_root, "target.duckdb");
        _replica = new DuckDbReplica(_targetPath);
        await _replica.BeginBootstrapAsync("source", [Orders], CancellationToken.None);
        await _replica.AppendBootstrapRowsAsync(
            Orders,
            [
                [Value(1), Value("new")],
                [Value(2), Value("new")],
            ],
            CancellationToken.None);
        await _replica.CompleteBootstrapAsync(
            "source",
            "acme",
            "analytics",
            snapshot: 1,
            schemaFingerprint: "schema-v1",
            CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked native handle should be visible in diagnostics, not turn cleanup into the
            // product assertion.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Snapshot_rows_and_checkpoint_commit_together()
    {
        await _replica.ApplySnapshotAsync(
            "source",
            snapshot: 2,
            schemaFingerprint: "schema-v1",
            [
                new ReplicaTableChanges(
                    Orders,
                    [
                        Change(10, "update_preimage", 1, "new"),
                        Change(10, "update_postimage", 1, "shipped"),
                        Change(11, "delete", 2, "new"),
                        Change(12, "insert", 3, "new"),
                    ]),
            ],
            CancellationToken.None);

        var rows = await ReadOrdersAsync();
        Assert.Equal([(1L, "shipped"), (3L, "new")], rows);
        Assert.Equal(2, (await _replica.GetCheckpointAsync("source", CancellationToken.None))!.LastAppliedSnapshot);

        // At-least-once replay becomes an exactly-once target effect.
        await _replica.ApplySnapshotAsync(
            "source",
            snapshot: 2,
            schemaFingerprint: "schema-v1",
            [],
            CancellationToken.None);
        Assert.Equal(rows, await ReadOrdersAsync());
    }

    [Fact]
    public async Task Failed_snapshot_rolls_back_rows_and_checkpoint()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _replica.ApplySnapshotAsync(
            "source",
            snapshot: 2,
            schemaFingerprint: "schema-v1",
            [
                new ReplicaTableChanges(
                    Orders,
                    [
                        Change(10, "insert", 3, "new"),
                        Change(11, "delete", 999, "missing"),
                    ]),
            ],
            CancellationToken.None));

        Assert.Contains("found 0", error.Message, StringComparison.Ordinal);
        Assert.Equal([(1L, "new"), (2L, "new")], await ReadOrdersAsync());
        Assert.Equal(1, (await _replica.GetCheckpointAsync("source", CancellationToken.None))!.LastAppliedSnapshot);
    }

    [Fact]
    public async Task Schema_change_and_snapshot_gap_fail_closed()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _replica.ApplySnapshotAsync(
            "source",
            snapshot: 2,
            schemaFingerprint: "schema-v2",
            [],
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _replica.ApplySnapshotAsync(
            "source",
            snapshot: 3,
            schemaFingerprint: "schema-v1",
            [],
            CancellationToken.None));

        Assert.Equal(1, (await _replica.GetCheckpointAsync("source", CancellationToken.None))!.LastAppliedSnapshot);
    }

    [Fact]
    public async Task Starting_rebootstrap_invalidates_the_completed_checkpoint_atomically()
    {
        await _replica.BeginBootstrapAsync("source", [Orders], CancellationToken.None);
        await _replica.AppendBootstrapRowsAsync(
            Orders,
            [[Value(99), Value("partial")]],
            CancellationToken.None);

        // Simulate a crash before CompleteBootstrapAsync. A subsequent worker must see no completed
        // checkpoint and restart bootstrap instead of applying CDC over the partial target.
        Assert.Null(await _replica.GetCheckpointAsync("source", CancellationToken.None));
        Assert.Equal([(99L, "partial")], await ReadOrdersAsync());
    }

    private async Task<IReadOnlyList<(long Id, string Status)>> ReadOrdersAsync()
    {
        await using var connection = new DuckDBConnection($"Data Source={_targetPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, status FROM main.orders ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(long, string)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        return rows;
    }

    private static ReplicaChange Change(long rowId, string type, long id, string status)
        => new(
            rowId,
            type,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["id"] = Value(id),
                ["status"] = Value(status),
            });

    private static JsonElement Value<T>(T value) => JsonSerializer.SerializeToElement(value);
}
