using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Lakehold.Engine.Catalog;

/// <summary>
///     Validation for identifiers that reach DuckDB through statements which cannot be
///     parameterised.
/// </summary>
/// <remarks>
///     <para>
///         Since provider 1.13.0 the provider builds and escapes the <c>ATTACH</c>, <c>USE</c>, and
///         maintenance statements itself, so Lakehold no longer quotes identifiers or escapes
///         literals by hand. What remains is validation at the trust boundary: catalog names
///         originate in tenant-editable control-plane records, and rejecting a malformed name before
///         it reaches the provider gives a clearer error than a failed <c>ATTACH</c> — and keeps the
///         check in place regardless of how the provider evolves.
///     </para>
///     <para>
///         Validation is allow-list based rather than escape-based: a name that is not obviously
///         safe is rejected, not sanitised.
///     </para>
/// </remarks>
public static class SqlIdentifier
{
    private const int MaxLength = 63;

    /// <summary>
    ///     Returns whether <paramref name="value"/> is a bare identifier: an ASCII letter or
    ///     underscore followed by ASCII letters, digits, or underscores.
    /// </summary>
    public static bool IsValid([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        if (!char.IsAsciiLetter(value[0]) && value[0] != '_')
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Validates <paramref name="value"/> as a bare identifier and returns it unchanged.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not a valid bare identifier.</exception>
    public static string Quote(string? value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid SQL identifier. Expected 1-{MaxLength} characters matching [A-Za-z_][A-Za-z0-9_]*.",
                paramName);
        }

        return value;
    }

    /// <summary>
    ///     Escapes <paramref name="value"/> as a double-quoted SQL identifier.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The counterpart to <see cref="Quote"/>, and used where the allow-list is the wrong
    ///         tool: a name that came <em>out</em> of the catalog and is being put back into a
    ///         statement. Tenants create tables called <c>order-items</c>, <c>my.table</c>, and
    ///         <c>select</c>, DuckLake stores all three, and refusing to name one is not a safety
    ///         property — it is a read that fails on a catalog the engine is perfectly happy with.
    ///     </para>
    ///     <para>
    ///         <see cref="Quote"/> stays the right choice at a trust boundary, where a malformed
    ///         name should be rejected rather than escaped. This one is for the other direction, and
    ///         escapes the way the SQL standard says: embedded double quotes are doubled. A NUL is
    ///         rejected outright, exactly as in <see cref="Literal"/>, because DuckDB's parser treats
    ///         it as a terminator and would silently truncate the statement.
    ///     </para>
    /// </remarks>
    public static string QuoteName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("SQL identifiers cannot contain NUL.", nameof(value));
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    /// <summary>
    ///     Escapes <paramref name="value"/> as a single-quoted SQL string literal.
    /// </summary>
    /// <remarks>
    ///     The provider parameterises everything it builds itself, but <c>COPY … TO '&lt;path&gt;'</c>
    ///     and <c>duckdb_databases()</c> filters take paths positionally with no bind-parameter
    ///     support. Those paths are operator-configured rather than tenant-supplied, but escaping
    ///     them is still cheaper than reasoning about who can influence a data path. Embedded single
    ///     quotes are doubled per the SQL standard; a NUL is rejected outright because DuckDB's
    ///     parser treats it as a terminator and would silently truncate the statement.
    /// </remarks>
    public static string Literal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("SQL string literals cannot contain NUL.", nameof(value));
        }

        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    /// <summary>
    ///     Validates a DuckDB extension name before it is passed to the provider's loader.
    /// </summary>
    public static string ValidateExtension(string? extension)
    {
        if (!IsValid(extension))
        {
            throw new ArgumentException(
                $"'{extension}' is not a valid DuckDB extension name.",
                nameof(extension));
        }

        return extension;
    }
}
