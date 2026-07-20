namespace Lakehold.Engine.Execution;

/// <summary>A column in a query result.</summary>
/// <param name="Name">Column name as returned by the engine.</param>
/// <param name="DataType">DuckDB type name, e.g. <c>BIGINT</c>, <c>VARCHAR</c>, <c>STRUCT(...)</c>.</param>
/// <param name="ClrType">
///     Name of the CLR type the provider materialises for this column. Surfacing both sides of the
///     mapping is what lets the UI right-align numerics and format wide integers correctly without
///     re-deriving DuckDB's type rules.
/// </param>
public sealed record ResultColumn(string Name, string DataType, string ClrType);

/// <summary>A materialised query result.</summary>
public sealed class QueryResult
{
    /// <summary>Column schema, in ordinal order.</summary>
    public required IReadOnlyList<ResultColumn> Columns { get; init; }

    /// <summary>Row values, each aligned to <see cref="Columns"/> by ordinal.</summary>
    public required IReadOnlyList<object?[]> Rows { get; init; }

    /// <summary>
    ///     Whether the engine stopped reading at the configured row ceiling. When true, the result
    ///     is a prefix of the full result set, not the whole of it.
    /// </summary>
    public required bool Truncated { get; init; }

    /// <summary>Server-side execution time.</summary>
    public required TimeSpan Elapsed { get; init; }
}
