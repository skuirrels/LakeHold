namespace Lakehold.Engine.Execution;

/// <summary>Splits SQL text into lexically distinct statements.</summary>
/// <remarks>
///     <para>
///         This is intentionally not a SQL parser. It only identifies semicolons that are outside
///         string literals, quoted identifiers, dollar-quoted bodies, and comments. That is enough
///         for callers that must enforce a single-statement boundary without trying to infer what
///         the statement means.
///     </para>
///     <para>
///         Keeping the lexical rule in the engine avoids two subtly different implementations in
///         the PostgreSQL-wire path and the saved-query publication path.
///     </para>
/// </remarks>
public static class SqlStatementSplitter
{
    /// <summary>Splits <paramref name="sql"/>, discarding empty trailing fragments.</summary>
    public static IReadOnlyList<string> Split(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var statements = new List<string>();
        var start = 0;
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            switch (c)
            {
                case '\'':
                case '"':
                    i = SkipQuoted(sql, i, c);
                    continue;

                case '$' when TryReadDollarTag(sql, i, out var tag):
                    i = SkipDollarQuoted(sql, i, tag);
                    continue;

                case '-' when i + 1 < sql.Length && sql[i + 1] == '-':
                    i = SkipLineComment(sql, i);
                    continue;

                case '/' when i + 1 < sql.Length && sql[i + 1] == '*':
                    i = SkipBlockComment(sql, i);
                    continue;

                case ';':
                    Add(statements, sql[start..i]);
                    i++;
                    start = i;
                    continue;

                default:
                    i++;
                    continue;
            }
        }

        Add(statements, sql[start..]);
        return statements;
    }

    private static void Add(List<string> statements, string candidate)
    {
        var trimmed = candidate.Trim();
        if (trimmed.Length > 0)
        {
            statements.Add(trimmed);
        }
    }

    private static int SkipQuoted(string sql, int index, char quote)
    {
        index++;
        while (index < sql.Length)
        {
            if (sql[index] != quote)
            {
                index++;
                continue;
            }

            if (index + 1 < sql.Length && sql[index + 1] == quote)
            {
                index += 2;
                continue;
            }

            return index + 1;
        }

        return index;
    }

    private static bool TryReadDollarTag(string sql, int index, out string tag)
    {
        tag = string.Empty;
        var end = index + 1;

        while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_'))
        {
            end++;
        }

        if (end >= sql.Length || sql[end] != '$')
        {
            return false;
        }

        tag = sql[index..(end + 1)];
        return true;
    }

    private static int SkipDollarQuoted(string sql, int index, string tag)
    {
        var close = sql.IndexOf(tag, index + tag.Length, StringComparison.Ordinal);
        return close < 0 ? sql.Length : close + tag.Length;
    }

    private static int SkipLineComment(string sql, int index)
    {
        var newline = sql.IndexOf('\n', index);
        return newline < 0 ? sql.Length : newline + 1;
    }

    private static int SkipBlockComment(string sql, int index)
    {
        var close = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
        return close < 0 ? sql.Length : close + 2;
    }
}
