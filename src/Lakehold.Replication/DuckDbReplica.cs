using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;

namespace Lakehold.Replication;

/// <summary>
///     Owns a dedicated DuckDB mirror. Source changes and the source checkpoint commit in the same
///     target transaction, producing exactly-once target effects from an at-least-once feed.
/// </summary>
public sealed partial class DuckDbReplica(string databasePath)
{
    private const string MetadataSchema = "_lakehold_replication";

    public async Task<ReplicaCheckpoint?> GetCheckpointAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureMetadataAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT source_id, tenant, catalog, bootstrap_snapshot, last_applied_snapshot,
                    schema_fingerprint, updated_utc
             FROM {MetadataSchema}.checkpoints
             WHERE source_id = ?
             """;
        Add(command, sourceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ReplicaCheckpoint(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetString(5),
            ReadTimestamp(reader.GetValue(6)));
    }

    public async Task BeginBootstrapAsync(
        string sourceId,
        IReadOnlyList<ReplicaTableDefinition> tables,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureMetadataAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await using (var invalidate = connection.CreateCommand())
            {
                invalidate.Transaction = transaction;
                invalidate.CommandText =
                    $"DELETE FROM {MetadataSchema}.checkpoints WHERE source_id = ?";
                Add(invalidate, sourceId);
                await invalidate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var table in tables)
            {
                Validate(table);
                await ExecuteAsync(
                        connection,
                        transaction,
                        $"CREATE SCHEMA IF NOT EXISTS {Quote(table.Schema)}",
                        cancellationToken)
                    .ConfigureAwait(false);
                await ExecuteAsync(
                        connection,
                        transaction,
                        $"DROP TABLE IF EXISTS {Qualified(table)}",
                        cancellationToken)
                    .ConfigureAwait(false);
                await ExecuteAsync(
                        connection,
                        transaction,
                        CreateTableSql(table),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task AppendBootstrapRowsAsync(
        ReplicaTableDefinition table,
        IReadOnlyList<JsonElement[]> rows,
        CancellationToken cancellationToken)
    {
        Validate(table);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var row in rows)
            {
                if (row.Length != table.Columns.Count)
                {
                    throw new InvalidOperationException(
                        $"Bootstrap row for {table.Schema}.{table.Table} has {row.Length} value(s), "
                        + $"expected {table.Columns.Count}.");
                }

                await InsertAsync(connection, transaction, table, row, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task CompleteBootstrapAsync(
        string sourceId,
        string tenant,
        string catalog,
        long snapshot,
        string schemaFingerprint,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureMetadataAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             INSERT INTO {MetadataSchema}.checkpoints
                 (source_id, tenant, catalog, bootstrap_snapshot, last_applied_snapshot,
                  schema_fingerprint, updated_utc)
             VALUES (?, ?, ?, ?, ?, ?, current_timestamp)
             ON CONFLICT (source_id) DO UPDATE SET
                 tenant = excluded.tenant,
                 catalog = excluded.catalog,
                 bootstrap_snapshot = excluded.bootstrap_snapshot,
                 last_applied_snapshot = excluded.last_applied_snapshot,
                 schema_fingerprint = excluded.schema_fingerprint,
                 updated_utc = excluded.updated_utc
             """;
        Add(command, sourceId);
        Add(command, tenant);
        Add(command, catalog);
        Add(command, snapshot);
        Add(command, snapshot);
        Add(command, schemaFingerprint);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplySnapshotAsync(
        string sourceId,
        long snapshot,
        string schemaFingerprint,
        IReadOnlyList<ReplicaTableChanges> tableChanges,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var checkpoint = await ReadCheckpointAsync(
                    connection,
                    transaction,
                    sourceId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Replica source '{sourceId}' has not been bootstrapped.");

            if (checkpoint.LastAppliedSnapshot >= snapshot)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (checkpoint.LastAppliedSnapshot + 1 != snapshot)
            {
                throw new InvalidOperationException(
                    $"Replica gap for '{sourceId}': checkpoint is {checkpoint.LastAppliedSnapshot}, "
                    + $"but snapshot {snapshot} was supplied.");
            }

            if (!string.Equals(checkpoint.SchemaFingerprint, schemaFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The source schema changed. This replica build fails closed and requires an "
                    + "explicit schema migration or re-bootstrap.");
            }

            foreach (var changes in tableChanges)
            {
                await ApplyTableAsync(connection, transaction, changes, cancellationToken).ConfigureAwait(false);
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                $"""
                 UPDATE {MetadataSchema}.checkpoints
                 SET last_applied_snapshot = ?, updated_utc = current_timestamp
                 WHERE source_id = ? AND last_applied_snapshot = ?
                 """;
            Add(update, snapshot);
            Add(update, sourceId);
            Add(update, snapshot - 1);
            var affected = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected != 1)
            {
                throw new InvalidOperationException("The replica checkpoint changed concurrently.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<long> CountRowsAsync(
        ReplicaTableDefinition table,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {Qualified(table)}";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task ApplyTableAsync(
        DuckDBConnection connection,
        DbTransaction transaction,
        ReplicaTableChanges tableChanges,
        CancellationToken cancellationToken)
    {
        var table = tableChanges.Table;
        Validate(table);

        if (table.Mode == ReplicaTableMode.AppendOnly)
        {
            foreach (var change in tableChanges.Changes)
            {
                if (!string.Equals(change.ChangeType, "insert", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Append-only replica table {table.Schema}.{table.Table} received "
                        + $"'{change.ChangeType}'.");
                }

                await InsertAsync(
                        connection,
                        transaction,
                        table,
                        ValuesInColumnOrder(table, change.Row),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        foreach (var group in tableChanges.Changes.GroupBy(change => change.RowId).OrderBy(group => group.Key))
        {
            var insert = group.SingleOrDefault(change => change.ChangeType == "insert");
            var delete = group.SingleOrDefault(change => change.ChangeType == "delete");
            var preimage = group.SingleOrDefault(change => change.ChangeType == "update_preimage");
            var postimage = group.SingleOrDefault(change => change.ChangeType == "update_postimage");
            var known = (insert is null ? 0 : 1)
                + (delete is null ? 0 : 1)
                + (preimage is null ? 0 : 1)
                + (postimage is null ? 0 : 1);
            if (known != group.Count())
            {
                throw new InvalidOperationException(
                    $"Replica table {table.Schema}.{table.Table} received an unknown or duplicate change type.");
            }

            if (preimage is not null || postimage is not null)
            {
                if (preimage is null || postimage is null || insert is not null || delete is not null)
                {
                    throw new InvalidOperationException(
                        $"Update row {group.Key} for {table.Schema}.{table.Table} is not a complete pre/post pair.");
                }

                await DeleteAsync(connection, transaction, table, preimage.Row, cancellationToken)
                    .ConfigureAwait(false);
                await InsertAsync(
                        connection,
                        transaction,
                        table,
                        ValuesInColumnOrder(table, postimage.Row),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (insert is not null)
            {
                await InsertAsync(
                        connection,
                        transaction,
                        table,
                        ValuesInColumnOrder(table, insert.Row),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (delete is not null)
            {
                await DeleteAsync(connection, transaction, table, delete.Row, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task DeleteAsync(
        DuckDBConnection connection,
        DbTransaction transaction,
        ReplicaTableDefinition table,
        IReadOnlyDictionary<string, JsonElement> row,
        CancellationToken cancellationToken)
    {
        var keys = table.KeyColumns.Select(key => Column(table, key)).ToArray();
        await using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText =
            $"SELECT count(*) FROM {Qualified(table)} WHERE "
            + string.Join(" AND ", keys.Select(column => $"{Quote(column.Name)} = CAST(? AS {column.DataType})"));
        foreach (var column in keys)
        {
            Add(count, ToParameter(row[column.Name], column.DataType));
        }

        var matches = Convert.ToInt64(
            await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (matches != 1)
        {
            throw new InvalidOperationException(
                $"Expected one target row for {table.Schema}.{table.Table} key, found {matches}.");
        }

        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText =
            $"DELETE FROM {Qualified(table)} WHERE "
            + string.Join(" AND ", keys.Select(column => $"{Quote(column.Name)} = CAST(? AS {column.DataType})"));
        foreach (var column in keys)
        {
            Add(delete, ToParameter(row[column.Name], column.DataType));
        }

        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertAsync(
        DuckDBConnection connection,
        DbTransaction transaction,
        ReplicaTableDefinition table,
        IReadOnlyList<JsonElement> values,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"INSERT INTO {Qualified(table)} ("
            + string.Join(", ", table.Columns.Select(column => Quote(column.Name)))
            + ") VALUES ("
            + string.Join(", ", table.Columns.Select(column => $"CAST(? AS {column.DataType})"))
            + ")";
        for (var index = 0; index < table.Columns.Count; index++)
        {
            Add(command, ToParameter(values[index], table.Columns[index].DataType));
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<JsonElement> ValuesInColumnOrder(
        ReplicaTableDefinition table,
        IReadOnlyDictionary<string, JsonElement> row)
        => [.. table.Columns.Select(column =>
            row.TryGetValue(column.Name, out var value)
                ? value
                : throw new InvalidOperationException(
                    $"Change row for {table.Schema}.{table.Table} omitted column '{column.Name}'."))];

    private static object ToParameter(JsonElement value, string dataType)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return DBNull.Value;
        }

        if (string.Equals(dataType, "BLOB", StringComparison.OrdinalIgnoreCase)
            && value.ValueKind == JsonValueKind.String)
        {
            return Convert.FromBase64String(value.GetString()!);
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when value.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.Number => value.GetDouble(),
            _ => value.GetRawText(),
        };
    }

    private static void Validate(ReplicaTableDefinition table)
    {
        if (table.Columns.Count == 0)
        {
            throw new ArgumentException("A replica table must have at least one column.", nameof(table));
        }

        foreach (var column in table.Columns)
        {
            _ = Quote(column.Name);
            if (!SupportedType().IsMatch(column.DataType))
            {
                throw new NotSupportedException(
                    $"DuckDB type '{column.DataType}' is not supported by the first replication release.");
            }
        }

        if (table.Mode == ReplicaTableMode.Keyed && table.KeyColumns.Count == 0)
        {
            throw new ArgumentException(
                $"Keyed replica table {table.Schema}.{table.Table} requires at least one key column.");
        }

        foreach (var key in table.KeyColumns)
        {
            _ = Column(table, key);
        }
    }

    private static ReplicaColumn Column(ReplicaTableDefinition table, string name)
        => table.Columns.FirstOrDefault(column => string.Equals(column.Name, name, StringComparison.Ordinal))
           ?? throw new ArgumentException(
               $"Replication key '{name}' is not a column of {table.Schema}.{table.Table}.");

    private static string CreateTableSql(ReplicaTableDefinition table)
    {
        var columns = table.Columns.Select(column =>
            $"{Quote(column.Name)} {column.DataType}{(column.IsNullable ? string.Empty : " NOT NULL")}");
        var constraints = table.Mode == ReplicaTableMode.Keyed
            ? columns.Append($"PRIMARY KEY ({string.Join(", ", table.KeyColumns.Select(Quote))})")
            : columns;
        return $"CREATE TABLE {Qualified(table)} ({string.Join(", ", constraints)})";
    }

    private static string Qualified(ReplicaTableDefinition table)
        => $"{Quote(table.Schema)}.{Quote(table.Table)}";

    private static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        if (identifier.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("DuckDB identifiers must not contain NUL.", nameof(identifier));
        }

        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private async Task<DuckDBConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new DuckDBConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static Task EnsureMetadataAsync(
        DuckDBConnection connection,
        CancellationToken cancellationToken)
        => EnsureMetadataAsync(connection, transaction: null, cancellationToken);

    private static async Task EnsureMetadataAsync(
        DuckDBConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
                connection,
                transaction,
                $"CREATE SCHEMA IF NOT EXISTS {MetadataSchema}",
                cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(
                connection,
                transaction,
                $"""
                 CREATE TABLE IF NOT EXISTS {MetadataSchema}.checkpoints (
                     source_id VARCHAR PRIMARY KEY,
                     tenant VARCHAR NOT NULL,
                     catalog VARCHAR NOT NULL,
                     bootstrap_snapshot BIGINT NOT NULL,
                     last_applied_snapshot BIGINT NOT NULL,
                     schema_fingerprint VARCHAR NOT NULL,
                     updated_utc TIMESTAMPTZ NOT NULL
                 )
                 """,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<ReplicaCheckpoint?> ReadCheckpointAsync(
        DuckDBConnection connection,
        DbTransaction transaction,
        string sourceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             SELECT source_id, tenant, catalog, bootstrap_snapshot, last_applied_snapshot,
                    schema_fingerprint, updated_utc
             FROM {MetadataSchema}.checkpoints
             WHERE source_id = ?
             """;
        Add(command, sourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ReplicaCheckpoint(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetString(5),
            ReadTimestamp(reader.GetValue(6)));
    }

    private static async Task ExecuteAsync(
        DuckDBConnection connection,
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Add(DbCommand command, object? value)
        => command.Parameters.Add(new DuckDBParameter { Value = value ?? DBNull.Value });

    private static DateTimeOffset ReadTimestamp(object value) => value switch
    {
        DateTimeOffset offset => offset,
        DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
        _ => DateTimeOffset.Parse(
            Convert.ToString(value, CultureInfo.InvariantCulture)!,
            CultureInfo.InvariantCulture),
    };

    [GeneratedRegex(
        @"^(?:BOOLEAN|TINYINT|SMALLINT|INTEGER|BIGINT|HUGEINT|UTINYINT|USMALLINT|UINTEGER|UBIGINT|UHUGEINT|REAL|FLOAT|DOUBLE|DECIMAL\(\d{1,2},\d{1,2}\)|VARCHAR|UUID|BLOB|DATE|TIME|TIMESTAMP|TIMESTAMP WITH TIME ZONE|TIMESTAMPTZ|INTERVAL)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SupportedType();
}
