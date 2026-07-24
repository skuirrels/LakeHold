using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Lakehold.ControlPlane.Data;

/// <summary>
///     Creates control-plane tables that exist in the model but not yet in the database.
/// </summary>
/// <remarks>
///     <para>
///         The control plane is initialised with <c>EnsureCreated</c>, which builds the full schema
///         on an empty database and does <em>nothing</em> on an existing one. That was fine while the
///         model never changed; the first release that adds an entity would leave every existing
///         deployment without its table, failing at first use rather than at start-up.
///     </para>
///     <para>
///         This applies the narrow, safe subset of a migration: statements from EF's own generated
///         create script that target tables missing from the live database. Purely additive — it
///         never alters or drops an existing table, and existing user state is never touched. Columns
///         added to an <em>existing</em> entity still need a real migration story; that trade-off is
///         documented in <see cref="ControlPlaneContext"/> and unchanged here.
///     </para>
/// </remarks>
public static class AdditiveSchema
{
    /// <summary>
    ///     Creates any model tables missing from the database, returning how many were created.
    /// </summary>
    public static async Task<int> EnsureModelTablesAsync(
        ControlPlaneContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var expected = context.Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var existing = await ListExistingTablesAsync(context, cancellationToken).ConfigureAwait(false);
        var missing = expected.Where(t => !existing.Contains(t)).ToArray();
        if (missing.Length == 0)
        {
            return 0;
        }

        // EF's own script is the source of truth for DDL, so the created table matches the model
        // exactly — hand-written DDL would drift the first time a property changed.
        var script = context.Database.GenerateCreateScript();
        var statements = script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Statements are executed in script order, which matters: an auto-increment column's DEFAULT
        // calls nextval() on a sequence that must already exist, and EF emits CREATE SEQUENCE ahead
        // of the CREATE TABLE that depends on it.
        //
        // Deduplicated because a statement can match more than one missing table — a foreign key
        // names both ends, so creating two related tables in one pass would otherwise run it twice.
        var executed = new HashSet<string>(StringComparer.Ordinal);
        var created = 0;

        foreach (var statement in statements)
        {
            var owner = missing.FirstOrDefault(t => BelongsTo(statement, t));
            if (owner is null || !executed.Add(statement))
            {
                continue;
            }

            await context.Database.ExecuteSqlRawAsync(statement, cancellationToken).ConfigureAwait(false);
        }

        foreach (var table in missing)
        {
            if (executed.Any(s => IsCreateTable(s, table)))
            {
                created++;
            }
        }

        return created;
    }

    /// <summary>
    ///     Adds columns present in the model but not yet in an existing table, returning how many were
    ///     added.
    /// </summary>
    /// <remarks>
    ///     The complement of <see cref="EnsureModelTablesAsync"/>: that one creates a whole table added
    ///     since a database was initialised, this one adds a column added to a table that already
    ///     exists — <c>QueryRun.TokenId</c> and <c>ApiToken.Role</c> are exactly this case. Only safe,
    ///     purely additive columns are handled: a nullable column, or a value-typed column with a
    ///     derivable default so existing rows get a value. A required column with no default is left
    ///     alone — it needs a real migration, and inventing a value for existing rows would be worse
    ///     than reporting the gap. Existing data is never rewritten.
    /// </remarks>
    public static async Task<int> EnsureModelColumnsAsync(
        ControlPlaneContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var existingTables = await ListExistingTablesAsync(context, cancellationToken).ConfigureAwait(false);
        var existingColumns = await ListExistingColumnsAsync(context, cancellationToken).ConfigureAwait(false);

        var added = 0;
        foreach (var entity in context.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (string.IsNullOrEmpty(table) || !existingTables.Contains(table))
            {
                // A missing table is EnsureModelTablesAsync's job; adding columns to it here would
                // race that, and a table just created already has every model column.
                continue;
            }

            var storeObject = StoreObjectIdentifier.Create(entity, StoreObjectType.Table);
            if (storeObject is not { } store)
            {
                continue;
            }

            var present = existingColumns.TryGetValue(table, out var set) ? set : [];

            foreach (var property in entity.GetProperties())
            {
                var column = property.GetColumnName(store);
                var type = property.GetColumnType(store);
                if (string.IsNullOrEmpty(column) || present.Contains(column) || string.IsNullOrEmpty(type))
                {
                    continue;
                }

                string ddl;
                if (property.IsNullable)
                {
                    ddl = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {type}";
                }
                else if (DefaultLiteralFor(property) is { } literal)
                {
                    // Existing rows need a value the moment the column is NOT NULL, so the default is
                    // the CLR default written back — 0 for a numeric or enum column, false for a bool.
                    ddl = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {type} DEFAULT {literal}";
                }
                else
                {
                    continue;
                }

                await context.Database.ExecuteSqlRawAsync(ddl, cancellationToken).ConfigureAwait(false);
                added++;
            }
        }

        return added;
    }

    /// <summary>The literal for a required column's default, or null when none can be derived safely.</summary>
    private static string? DefaultLiteralFor(IProperty property)
    {
        var clr = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;

        if (clr.IsEnum)
        {
            return "0";
        }

        if (clr == typeof(bool))
        {
            return "false";
        }

        return clr == typeof(int) || clr == typeof(long) || clr == typeof(short) || clr == typeof(byte)
            || clr == typeof(decimal) || clr == typeof(double) || clr == typeof(float)
            ? "0"
            : null;
    }

    private static async Task<Dictionary<string, HashSet<string>>> ListExistingColumnsAsync(
        ControlPlaneContext context,
        CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        var connection = context.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT table_name, column_name FROM information_schema.columns WHERE table_schema = 'main'";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var table = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
                var column = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture);
                if (table is { Length: > 0 } && column is { Length: > 0 })
                {
                    if (!found.TryGetValue(table, out var columns))
                    {
                        columns = new HashSet<string>(StringComparer.Ordinal);
                        found[table] = columns;
                    }

                    columns.Add(column);
                }
            }
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }

        return found;
    }

    /// <summary>Whether a DDL statement is part of <paramref name="table"/>'s definition.</summary>
    /// <remarks>
    ///     Matching on the quoted table name alone is not enough, and getting this wrong is silent:
    ///     a sequence is named <c>"&lt;Table&gt;_&lt;Column&gt;_seq"</c>, which contains
    ///     <c>"Table</c> but never <c>"Table"</c> — the closing quote is what breaks it. Missing the
    ///     sequence let the CREATE TABLE through with a DEFAULT calling a nextval() on something that
    ///     did not exist, failing at start-up on exactly the upgrade this class exists to serve.
    ///     Sequences are therefore matched on the <c>"&lt;Table&gt;_</c> prefix instead.
    /// </remarks>
    private static bool BelongsTo(string statement, string table)
        => statement.Contains($"\"{table}\"", StringComparison.Ordinal)
           || (statement.StartsWith("CREATE SEQUENCE", StringComparison.OrdinalIgnoreCase)
               && statement.Contains($"\"{table}_", StringComparison.Ordinal));

    private static bool IsCreateTable(string statement, string table)
        => statement.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase)
           && statement.Contains($"\"{table}\"", StringComparison.Ordinal);

    private static async Task<HashSet<string>> ListExistingTablesAsync(
        ControlPlaneContext context,
        CancellationToken cancellationToken)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);

        var connection = context.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = 'main'";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) is { Length: > 0 } name)
                {
                    found.Add(name);
                }
            }
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }

        return found;
    }
}
