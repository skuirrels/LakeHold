using System.Globalization;
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
public sealed record ChangeFeedPage(
    string Schema,
    string Table,
    long FromSnapshot,
    long ToSnapshot,
    IReadOnlyList<TableChange> Changes,
    bool Truncated);

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
                    Table: Convert.ToString(r[1], CultureInfo.InvariantCulture) ?? string.Empty))
                .Where(t => SqlIdentifier.IsValid(t.Schema) && SqlIdentifier.IsValid(t.Table)),
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
    {
        ArgumentNullException.ThrowIfNull(duckling);
        _ = SqlIdentifier.Quote(schema, nameof(schema));
        _ = SqlIdentifier.Quote(table, nameof(table));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);

        // An empty or inverted range is a no-op rather than an error: the poller reaches it whenever a
        // catalog has committed nothing since the last delivery.
        if (toSnapshot < fromSnapshot || fromSnapshot < 0)
        {
            return new ChangeFeedPage(schema, table, fromSnapshot, toSnapshot, [], Truncated: false);
        }

        var catalog = SqlIdentifier.Literal(duckling.Catalog.CatalogName);
        var start = fromSnapshot.ToString(CultureInfo.InvariantCulture);
        var end = toSnapshot.ToString(CultureInfo.InvariantCulture);

        // Fetch one more than the ceiling so truncation is detectable. ORDER BY before LIMIT makes the
        // cut deterministic, so a truncated page is always a prefix and the next read can resume from
        // where it stopped rather than guessing.
        var sql =
            $"SELECT * FROM ducklake_table_changes({catalog}, {SqlIdentifier.Literal(schema)}, " +
            $"{SqlIdentifier.Literal(table)}, {start}, {end}) " +
            $"ORDER BY snapshot_id, rowid LIMIT {(maxRows + 1).ToString(CultureInfo.InvariantCulture)}";

        var result = await duckling.ExecuteQueryAsync(sql, cancellationToken).ConfigureAwait(false);

        var snapshotIndex = IndexOf(result.Columns, "snapshot_id");
        var rowIdIndex = IndexOf(result.Columns, "rowid");
        var changeTypeIndex = IndexOf(result.Columns, "change_type");

        // The table's data columns are everything the feed adds on top of its three bookkeeping
        // columns. Captured by index so the row projection skips them without string comparisons.
        var dataColumns = result.Columns
            .Select((c, i) => (c.Name, i))
            .Where(c => c.i != snapshotIndex && c.i != rowIdIndex && c.i != changeTypeIndex)
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

        return new ChangeFeedPage(schema, table, fromSnapshot, toSnapshot, changes, truncated);
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
}
