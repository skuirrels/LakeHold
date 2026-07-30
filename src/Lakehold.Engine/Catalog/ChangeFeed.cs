using System.Globalization;
using System.Text.Json;
using Lakehold.Engine.Execution;

namespace Lakehold.Engine.Catalog;

/// <summary>How a row changed between two snapshots.</summary>
/// <remarks>
///     DuckLake models an update as a delete of the old row and an insert of the new one, surfaced as
///     a paired <see cref="UpdatePreimage"/> (the old values) and <see cref="UpdatePostimage"/> (the
///     new values) sharing a <c>rowid</c>. A consumer that only wants net effect can treat preimage as
///     a delete and postimage as an insert; one that wants to diff can pair them.
/// </remarks>
public enum ChangeType
{
    /// <summary>A newly inserted row.</summary>
    Insert,

    /// <summary>A deleted row.</summary>
    Delete,

    /// <summary>The prior values of an updated row.</summary>
    UpdatePreimage,

    /// <summary>The new values of an updated row.</summary>
    UpdatePostimage,

    /// <summary>A change type this build does not recognise. Forwarded verbatim rather than dropped.</summary>
    Unknown,
}

/// <summary>One row-level change from a table's change feed.</summary>
/// <param name="SnapshotId">The snapshot that committed the change.</param>
/// <param name="RowId">DuckLake's stable row identity, pairing an update's pre- and post-image.</param>
/// <param name="Change">The kind of change.</param>
/// <param name="Row">
///     The table's own columns for this change, already projected to JSON-safe wire values. Excludes
///     the feed's <c>snapshot_id</c>, <c>rowid</c>, and <c>change_type</c> bookkeeping columns.
/// </param>
public sealed record TableChange(
    long SnapshotId,
    long RowId,
    ChangeType Change,
    IReadOnlyDictionary<string, object?> Row);

/// <summary>A page of changes for one table across a snapshot range.</summary>
/// <param name="Schema">Schema of the table.</param>
/// <param name="Table">Table name.</param>
/// <param name="FromSnapshot">Inclusive lower bound of the range read.</param>
/// <param name="ToSnapshot">Inclusive upper bound of the range read.</param>
/// <param name="Changes">The changes, ordered by snapshot then row.</param>
/// <param name="Truncated">Whether the page hit the row ceiling and omitted later changes.</param>
/// <param name="NextCursor">
///     Opaque position immediately after the last returned change, or <see langword="null"/> when
///     the requested range is complete.
/// </param>
public sealed record ChangeFeedPage(
    string Schema,
    string Table,
    long FromSnapshot,
    long ToSnapshot,
    IReadOnlyList<TableChange> Changes,
    bool Truncated,
    string? NextCursor = null);

/// <summary>
///     Reads DuckLake's built-in change feed — change data capture with no Debezium, no Kafka, and no
///     separate pipeline.
/// </summary>
/// <remarks>
///     <para>
///         DuckLake records the changes each snapshot made, exposed through
///         <c>ducklake_table_changes(catalog, schema, table, start, end)</c>. Verified on DuckDB 1.5.4:
///         the range is inclusive at both ends; a table created inside the range contributes only from
///         its creation; a table with no changes in the range returns empty rather than erroring. The
///         feed's shape is <c>snapshot_id</c>, <c>rowid</c>, <c>change_type</c>, then the table's own
///         columns, with <c>change_type</c> one of <c>insert</c>, <c>delete</c>,
///         <c>update_preimage</c>, or <c>update_postimage</c>.
///     </para>
///     <para>
///         This is the source both the typed .NET feed and the outbound webhook dispatcher read from.
///         Because the range is inclusive, a caller that has already delivered up to snapshot
///         <c>L</c> reads the next batch from <c>L + 1</c> so a change is never delivered twice.
///     </para>
/// </remarks>
public static class ChangeFeed
{
    /// <summary>Returns the newest snapshot id, or null when the catalog has no snapshots.</summary>
    public static async Task<long?> LatestSnapshotAsync(Duckling duckling, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);

        var result = await duckling
            .ExecuteQueryAsync(
                $"SELECT max(snapshot_id) FROM ducklake_snapshots({SqlIdentifier.Literal(duckling.Catalog.CatalogName)})",
                cancellationToken)
            .ConfigureAwait(false);

        return ToInt64Nullable(result.Rows.Count > 0 ? result.Rows[0][0] : null);
    }

    /// <summary>Lists the catalog's base tables, so a subscription can fan out across all of them.</summary>
    public static async Task<IReadOnlyList<(string Schema, string Table)>> ListTablesAsync(
        Duckling duckling,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);

        var result = await duckling
            .ExecuteQueryAsync(
                "SELECT schema_name, table_name FROM duckdb_tables() " +
                $"WHERE database_name = {SqlIdentifier.Literal(duckling.Catalog.CatalogName)} " +
                "AND table_name NOT LIKE 'ducklake\\_%' ESCAPE '\\' " +
                "ORDER BY schema_name, table_name",
                cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. result.Rows
                .Select(r => (
                    Schema: Convert.ToString(r[0], CultureInfo.InvariantCulture) ?? string.Empty,
                    Table: Convert.ToString(r[1], CultureInfo.InvariantCulture) ?? string.Empty)),
        ];
    }

    /// <summary>
    ///     Reads a table's changes over the inclusive snapshot range
    ///     <paramref name="fromSnapshot"/>..<paramref name="toSnapshot"/>.
    /// </summary>
    /// <param name="duckling">The session whose catalog owns the table.</param>
    /// <param name="schema">Schema of the table.</param>
    /// <param name="table">Table name.</param>
    /// <param name="fromSnapshot">Inclusive lower bound.</param>
    /// <param name="toSnapshot">Inclusive upper bound.</param>
    /// <param name="maxRows">Ceiling on returned changes; a full page sets <c>Truncated</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<ChangeFeedPage> ReadAsync(
        Duckling duckling,
        string schema,
        string table,
        long fromSnapshot,
        long toSnapshot,
        int maxRows,
        CancellationToken cancellationToken)
        => await ReadAsync(
                duckling,
                schema,
                table,
                fromSnapshot,
                toSnapshot,
                maxRows,
                cursor: null,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    ///     Reads one bounded page of changes and resumes after an opaque cursor returned by the same
    ///     table and snapshot range.
    /// </summary>
    public static async Task<ChangeFeedPage> ReadAsync(
        Duckling duckling,
        string schema,
        string table,
        long fromSnapshot,
        long toSnapshot,
        int maxRows,
        string? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);

        // An empty or inverted range is a no-op rather than an error: the poller reaches it whenever a
        // catalog has committed nothing since the last delivery.
        if (toSnapshot < fromSnapshot || fromSnapshot < 0)
        {
            return new ChangeFeedPage(schema, table, fromSnapshot, toSnapshot, [], Truncated: false);
        }

        var position = cursor is null
            ? null
            : ChangeFeedCursor.Decode(cursor, schema, table, fromSnapshot, toSnapshot);
        var catalog = SqlIdentifier.Literal(duckling.Catalog.CatalogName);
        var start = fromSnapshot.ToString(CultureInfo.InvariantCulture);
        var end = toSnapshot.ToString(CultureInfo.InvariantCulture);
        var after = position is null
            ? string.Empty
            : "WHERE (snapshot_id, rowid, _lakehold_change_order, change_type) > "
              + $"({position.SnapshotId.ToString(CultureInfo.InvariantCulture)}, "
              + $"{position.RowId.ToString(CultureInfo.InvariantCulture)}, "
              + $"{position.ChangeOrder.ToString(CultureInfo.InvariantCulture)}, "
              + $"{SqlIdentifier.Literal(position.ChangeType)}) ";

        // Fetch one more than the ceiling so truncation is detectable. The explicit change ordering
        // keeps an update's pre-image before its post-image and gives keyset pagination a stable
        // position even when both halves share snapshot_id and rowid.
        var sql =
            "SELECT * FROM ("
            + "SELECT *, CASE change_type "
            + "WHEN 'insert' THEN 0 WHEN 'delete' THEN 1 "
            + "WHEN 'update_preimage' THEN 2 WHEN 'update_postimage' THEN 3 ELSE 4 END "
            + "AS _lakehold_change_order "
            + $"FROM ducklake_table_changes({catalog}, {SqlIdentifier.Literal(schema)}, "
            + $"{SqlIdentifier.Literal(table)}, {start}, {end})) AS lakehold_changes "
            + after
            + "ORDER BY snapshot_id, rowid, _lakehold_change_order, change_type "
            + $"LIMIT {(maxRows + 1).ToString(CultureInfo.InvariantCulture)}";

        var result = await duckling.ExecuteQueryAsync(sql, cancellationToken).ConfigureAwait(false);

        var snapshotIndex = IndexOf(result.Columns, "snapshot_id");
        var rowIdIndex = IndexOf(result.Columns, "rowid");
        var changeTypeIndex = IndexOf(result.Columns, "change_type");
        var changeOrderIndex = IndexOf(result.Columns, "_lakehold_change_order");

        // The table's data columns are everything the feed adds on top of its three bookkeeping
        // columns. Captured by index so the row projection skips them without string comparisons.
        var dataColumns = result.Columns
            .Select((c, i) => (c.Name, i))
            .Where(c =>
                c.i != snapshotIndex
                && c.i != rowIdIndex
                && c.i != changeTypeIndex
                && c.i != changeOrderIndex)
            .ToArray();

        var truncated = result.Rows.Count > maxRows || result.Truncated;
        var take = Math.Min(result.Rows.Count, maxRows);

        var changes = new List<TableChange>(take);
        for (var r = 0; r < take; r++)
        {
            var row = result.Rows[r];

            var values = new Dictionary<string, object?>(dataColumns.Length, StringComparer.Ordinal);
            foreach (var (name, index) in dataColumns)
            {
                values[name] = row[index];
            }

            changes.Add(new TableChange(
                ToInt64Nullable(row[snapshotIndex]) ?? 0,
                ToInt64Nullable(row[rowIdIndex]) ?? 0,
                ParseChangeType(Convert.ToString(row[changeTypeIndex], CultureInfo.InvariantCulture)),
                values));
        }

        string? nextCursor = null;
        if (truncated && take > 0)
        {
            var last = result.Rows[take - 1];
            nextCursor = ChangeFeedCursor.Encode(
                schema,
                table,
                fromSnapshot,
                toSnapshot,
                ToInt64Nullable(last[snapshotIndex]) ?? 0,
                ToInt64Nullable(last[rowIdIndex]) ?? 0,
                Convert.ToInt32(last[changeOrderIndex], CultureInfo.InvariantCulture),
                Convert.ToString(last[changeTypeIndex], CultureInfo.InvariantCulture) ?? string.Empty);
        }

        return new ChangeFeedPage(schema, table, fromSnapshot, toSnapshot, changes, truncated, nextCursor);
    }

    private static int IndexOf(IReadOnlyList<ResultColumn> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new InvalidOperationException(
            $"The change feed did not include a '{name}' column. Columns: " +
            $"{string.Join(", ", columns.Select(c => c.Name))}.");
    }

    private static ChangeType ParseChangeType(string? value) => value switch
    {
        "insert" => ChangeType.Insert,
        "delete" => ChangeType.Delete,
        "update_preimage" => ChangeType.UpdatePreimage,
        "update_postimage" => ChangeType.UpdatePostimage,
        _ => ChangeType.Unknown,
    };

    private static long? ToInt64Nullable(object? value) => value switch
    {
        null => null,
        long l => l,
        // Duckling projects wide integers to strings for JSON safety before they reach here.
        string s when long.TryParse(s, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture),
    };

    private sealed record ChangeFeedCursorPayload(
        int Version,
        string Schema,
        string Table,
        long FromSnapshot,
        long ToSnapshot,
        long SnapshotId,
        long RowId,
        int ChangeOrder,
        string ChangeType);

    private static class ChangeFeedCursor
    {
        private const int CurrentVersion = 1;

        public static string Encode(
            string schema,
            string table,
            long fromSnapshot,
            long toSnapshot,
            long snapshotId,
            long rowId,
            int changeOrder,
            string changeType)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(new ChangeFeedCursorPayload(
                CurrentVersion,
                schema,
                table,
                fromSnapshot,
                toSnapshot,
                snapshotId,
                rowId,
                changeOrder,
                changeType));
            return Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static ChangeFeedCursorPayload Decode(
            string cursor,
            string schema,
            string table,
            long fromSnapshot,
            long toSnapshot)
        {
            try
            {
                var base64 = cursor.Replace('-', '+').Replace('_', '/');
                base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
                var payload = JsonSerializer.Deserialize<ChangeFeedCursorPayload>(
                    Convert.FromBase64String(base64));

                if (payload is null
                    || payload.Version != CurrentVersion
                    || !string.Equals(payload.Schema, schema, StringComparison.Ordinal)
                    || !string.Equals(payload.Table, table, StringComparison.Ordinal)
                    || payload.FromSnapshot != fromSnapshot
                    || payload.ToSnapshot != toSnapshot
                    || payload.SnapshotId < fromSnapshot
                    || payload.SnapshotId > toSnapshot
                    || payload.ChangeOrder is < 0 or > 4)
                {
                    throw new ArgumentException("The change-feed cursor does not belong to this table and snapshot range.");
                }

                return payload;
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                throw new ArgumentException("The change-feed cursor is invalid.", nameof(cursor), ex);
            }
        }
    }
}
