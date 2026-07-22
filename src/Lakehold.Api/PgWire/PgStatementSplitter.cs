namespace Lakehold.Api.PgWire;

/// <summary>
///     Splits a simple-query message body into its individual statements.
/// </summary>
/// <remarks>
///     <para>
///         The simple query protocol allows several statements in one message, and the server must
///         answer with a result set per statement followed by a single <c>ReadyForQuery</c>. This is
///         not an edge case: Npgsql's type-catalogue load arrives as four statements in one message,
///         and a server that executes the text as a single statement desynchronises the client
///         immediately — it waits for a second <c>RowDescription</c> that never comes.
///     </para>
///     <para>
///         Splitting SQL by scanning for semicolons is normally a bad idea, and it is worth being
///         precise about why it is acceptable here. This is not parsing: the split is purely lexical
///         and only needs to know when a semicolon is <em>inside</em> something. Everything it skips
///         — string literals, quoted identifiers, dollar-quoted bodies, and both comment styles — is
///         a lexical construct with an unambiguous terminator. Nothing about the statement's meaning
///         is inspected or rewritten, so this never becomes the SQL-parsing security boundary that
///         invariant 4 rules out.
///     </para>
/// </remarks>
internal static class PgStatementSplitter
{
    /// <summary>Splits <paramref name="sql"/>, discarding empty trailing fragments.</summary>
    public static List<string> Split(string sql)
    {
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

    /// <summary>
    ///     Skips a single- or double-quoted run, honouring the doubled-quote escape.
    /// </summary>
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

            // A doubled quote is an escaped quote, not the end of the run.
            if (index + 1 < sql.Length && sql[index + 1] == quote)
            {
                index += 2;
                continue;
            }

            return index + 1;
        }

        return index;
    }

    /// <summary>
    ///     Reads a dollar-quote opening tag — <c>$$</c> or <c>$tag$</c> — if one starts here.
    /// </summary>
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
