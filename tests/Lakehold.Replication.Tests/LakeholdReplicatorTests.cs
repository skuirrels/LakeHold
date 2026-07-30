using System.Net;
using System.Text;
using System.Text.Json;
using DuckDB.NET.Data;
using Lakehold.Client;
using Lakehold.Replication;
using Xunit;

namespace Lakehold.Replication.Tests;

public sealed class LakeholdReplicatorTests
{
    [Fact]
    public async Task Bootstrap_and_next_snapshot_reproduce_the_source_and_ack_after_commit()
    {
        var targetPath = Path.Combine(
            Path.GetTempPath(),
            "lakehold-replicator-tests",
            $"{Guid.NewGuid():N}.duckdb");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        try
        {
            var sourceHandler = new SourceHandler();
            using var http = new HttpClient(sourceHandler)
            {
                BaseAddress = new Uri("https://lakehold.test"),
            };
            var client = new LakeholdClient(http, "test-token");
            var target = new DuckDbReplica(targetPath);
            var replicator = new LakeholdReplicator(
                client,
                target,
                new ReplicaSource(
                    "orders-mirror",
                    "acme",
                    "analytics",
                    [
                        new ReplicaTableSelection(
                            "main",
                            "orders",
                            ReplicaTableMode.Keyed,
                            ["id"]),
                    ],
                    PageSize: 100));

            var checkpoint = await replicator.ReplicateOnceAsync(CancellationToken.None);

            Assert.Equal(3, checkpoint.LastAppliedSnapshot);
            Assert.Equal([2L, 3L], sourceHandler.AcknowledgedSnapshots);

            await using var connection = new DuckDBConnection($"Data Source={targetPath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, status FROM main.orders ORDER BY id";
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<(long Id, string Status)>();
            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }

            Assert.Equal([(1L, "shipped"), (2L, "new"), (3L, "new")], rows);
        }
        finally
        {
            File.Delete(targetPath);
        }
    }

    private sealed class SourceHandler : HttpMessageHandler
    {
        private int _snapshotReads;

        public List<long> AcknowledgedSnapshots { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path.Contains("/snapshots?limit=1", StringComparison.Ordinal))
            {
                var snapshot = Interlocked.Increment(ref _snapshotReads) == 1 ? 2 : 3;
                return Json(
                    $$"""[{"snapshotId":{{snapshot}},"committedAt":"2026-07-30T12:00:00Z","schemaVersion":1,"commitMessage":null}]""");
            }

            if (path.EndsWith("/schemas", StringComparison.Ordinal))
            {
                return Json(
                    """
                    [{
                      "name":"main",
                      "tables":[{
                        "name":"orders",
                        "kind":"table",
                        "columns":[
                          {"name":"id","dataType":"BIGINT","isNullable":false},
                          {"name":"status","dataType":"VARCHAR","isNullable":false}
                        ]
                      }]
                    }]
                    """);
            }

            if (path.EndsWith("/query", StringComparison.Ordinal))
            {
                var requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                var sql = JsonDocument.Parse(requestBody).RootElement.GetProperty("sql").GetString()!;
                return sql.StartsWith("SELECT count(*)", StringComparison.Ordinal)
                    ? QueryPage(
                        """[{"name":"row_count","dataType":"BIGINT","clrType":"System.Int64"}]""",
                        "[[2]]")
                    : QueryPage(
                        """
                        [
                          {"name":"id","dataType":"BIGINT","clrType":"System.Int64"},
                          {"name":"status","dataType":"VARCHAR","clrType":"System.String"}
                        ]
                        """,
                        """[[1,"new"],[2,"new"]]""");
            }

            if (path.Contains("/cdc/snapshots/3/changes", StringComparison.Ordinal))
            {
                return Json(
                    """
                    {
                      "schema":"main",
                      "table":"orders",
                      "fromSnapshot":3,
                      "toSnapshot":3,
                      "truncated":false,
                      "nextCursor":null,
                      "changes":[
                        {"snapshotId":3,"rowId":10,"changeType":"update_preimage","row":{"id":1,"status":"new"}},
                        {"snapshotId":3,"rowId":10,"changeType":"update_postimage","row":{"id":1,"status":"shipped"}},
                        {"snapshotId":3,"rowId":11,"changeType":"insert","row":{"id":3,"status":"new"}}
                      ]
                    }
                    """);
            }

            if (path.EndsWith("/cdc/consumers", StringComparison.Ordinal))
            {
                return Json(
                    """
                    {
                      "id":7,
                      "name":"orders-mirror",
                      "catalog":"analytics",
                      "lastAppliedSnapshot":1,
                      "active":true,
                      "createdUtc":"2026-07-30T12:00:00Z",
                      "updatedUtc":"2026-07-30T12:00:00Z"
                    }
                    """);
            }

            if (path.EndsWith("/cdc/consumers/7/checkpoint", StringComparison.Ordinal))
            {
                var requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                var checkpoint = JsonDocument.Parse(requestBody)
                    .RootElement
                    .GetProperty("lastAppliedSnapshot")
                    .GetInt64();
                AcknowledgedSnapshots.Add(checkpoint);
                return Json(
                    $$"""
                    {
                      "id":7,
                      "name":"orders-mirror",
                      "catalog":"analytics",
                      "lastAppliedSnapshot":{{checkpoint}},
                      "active":true,
                      "createdUtc":"2026-07-30T12:00:00Z",
                      "updatedUtc":"2026-07-30T12:00:00Z"
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(path, Encoding.UTF8, "text/plain"),
            };
        }

        private static HttpResponseMessage QueryPage(string columns, string rows)
            => Json(
                $$"""
                {
                  "columns":{{columns}},
                  "rows":{{rows}},
                  "truncated":false,
                  "elapsedMilliseconds":1,
                  "rowsAffected":null
                }
                """);

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }
}
