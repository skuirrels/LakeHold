namespace Lakehold.Engine.Execution;

/// <summary>
///     Reads the leading keyword of a submitted statement, and decides whether that statement's
///     result is an affected-row count rather than a set of rows.
/// </summary>
/// <remarks>
///     <para>
///         This is a reporting concern, never a security one. Credential resolution chooses the
///         tenant-qualified catalog, while arbitrary SQL requires a separate containment boundary.
///         Nothing here filters, rewrites, or authorises SQL — it only chooses which execution path
///         reports the statement's outcome honestly. A statement this
///         cannot classify takes the ordinary streaming path, which is what every statement did
///         before, so the failure mode is a missing count rather than a wrong result.
///     </para>
///     <para>
///         The classification exists because the provider's dynamic API has no affected-row count:
///         DuckDB.NET's data reader reports <c>RecordsAffected == -1</c>, so a DML statement run
///         through <c>SqlQueryDynamicRawAsync</c> yields no columns and no rows and is
///         indistinguishable from a statement that returned nothing. <c>ExecuteNonQuery</c> on the
///         same connection does report the count, which is the path
///         <see cref="Duckling.ExecuteQueryAsync"/> takes for the verbs below.
///     </para>
/// </remarks>
public static class StatementVerb
{
    /// <summary>
    ///     Returns the statement's leading keyword in upper case, skipping leading whitespace and
    ///     comments, or an empty string when the statement has no keyword.
    /// </summary>
    public static string Of(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var span = SkipLeading(sql.AsSpan());
        if (span.IsEmpty)
        {
            return string.Empty;
        }

        var end = 0;
        while (end < span.Length && (char.IsLetter(span[end]) || span[end] == '_'))
        {
            end++;
        }

        return end == 0 ? string.Empty : span[..end].ToString().ToUpperInvariant();
    }

    /// <summary>
    ///     Whether this statement's outcome is an affected-row count, so it should be executed as a
    ///     non-query rather than streamed.
    /// </summary>
    /// <remarks>
    ///     A statement carrying <c>RETURNING</c> is excluded: its rows are the result, and running it
    ///     as a non-query would execute it and then discard them. DuckLake rejects <c>RETURNING</c>
    ///     outright, so the exclusion is a guard rather than a live path — but the cost of being
    ///     wrong here is a silently empty result, which is worth a keyword search to avoid. The
    ///     search does not exclude string literals, so a statement with the word in a value falls
    ///     back to streaming and simply reports no count.
    /// </remarks>
    public static bool ReportsAffectedRows(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var verb = Of(sql);
        if (verb is not ("INSERT" or "UPDATE" or "DELETE" or "MERGE"))
        {
            return false;
        }

        return !ContainsWord(sql, "RETURNING");
    }

    /// <summary>
    ///     Statements whose leading keyword means the catalog cannot have advanced.
    /// </summary>
    /// <remarks>
    ///     Deliberately an allow-list of <em>reads</em>, so the unknown case falls on the safe side.
    ///     Getting this wrong in the other direction discards a warm session for nothing; getting it
    ///     wrong in this direction serves a caller data from before their own write.
    /// </remarks>
    private static readonly HashSet<string> NonCommitting = new(StringComparer.Ordinal)
    {
        "SELECT", "WITH", "FROM", "VALUES", "TABLE",
        "DESCRIBE", "SHOW", "EXPLAIN", "SUMMARIZE", "PRAGMA",
    };

    /// <summary>
    ///     Keywords that make a statement a write however it begins.
    /// </summary>
    /// <remarks>
    ///     Needed because <c>WITH</c> is in the read list and DuckDB accepts a CTE in front of a
    ///     write: <c>WITH x AS (…) INSERT INTO t SELECT * FROM x</c> leads with <c>WITH</c> and
    ///     commits. Scanning for these as whole words is the same technique
    ///     <see cref="ReportsAffectedRows"/> already uses for <c>RETURNING</c>, and it is safe in the
    ///     same way: the scan does not exclude string literals, so a read whose text happens to
    ///     contain one of these words is treated as a write and costs a warm session, which is the
    ///     side to be wrong on.
    /// </remarks>
    private static readonly string[] CommittingKeywords =
    [
        "INSERT", "UPDATE", "DELETE", "MERGE", "CREATE", "DROP", "ALTER",
        "TRUNCATE", "COPY", "ATTACH", "DETACH", "CALL", "INSTALL", "LOAD",
    ];

    /// <summary>
    ///     Whether this statement could have advanced the catalog to a new snapshot.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Like <see cref="ReportsAffectedRows"/> this reads the leading keyword only, and like it
    ///         this is a bookkeeping concern rather than a security one — it decides whether other
    ///         warm sessions for the catalog still hold a current view of it, never whether a
    ///         statement is permitted.
    ///     </para>
    ///     <para>
    ///         It exists because a DuckLake catalog attached read-only does not observe commits made
    ///         through a separate read-write attachment, and LakeHold keeps those in separate pooled
    ///         sessions on purpose (invariant 20). Without this, an agent that wrote through
    ///         <c>execute</c> and read back through <c>query</c> was answered from the snapshot its
    ///         read-only session attached at — silently, and for as long as that session stayed warm.
    ///     </para>
    /// </remarks>
    public static bool MayCommit(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var verb = Of(sql);
        if (verb.Length == 0 || !NonCommitting.Contains(verb))
        {
            return true;
        }

        // The leading keyword says "read", but a CTE can precede a write, and a submission may carry
        // more than one statement. Confirm no committing keyword appears anywhere before trusting it.
        foreach (var keyword in CommittingKeywords)
        {
            if (ContainsWord(sql, keyword))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Skips whitespace, <c>--</c> line comments, and <c>/* */</c> block comments.</summary>
    private static ReadOnlySpan<char> SkipLeading(ReadOnlySpan<char> span)
    {
        while (true)
        {
            span = span.TrimStart();

            if (span.StartsWith("--", StringComparison.Ordinal))
            {
                var newline = span.IndexOf('\n');
                span = newline < 0 ? [] : span[(newline + 1)..];
                continue;
            }

            if (span.StartsWith("/*", StringComparison.Ordinal))
            {
                var close = span.IndexOf("*/", StringComparison.Ordinal);
                span = close < 0 ? [] : span[(close + 2)..];
                continue;
            }

            return span;
        }
    }

    /// <summary>Whether <paramref name="word"/> appears in <paramref name="sql"/> as a whole word.</summary>
    private static bool ContainsWord(string sql, string word)
    {
        var index = 0;
        while ((index = sql.IndexOf(word, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var beforeOk = index == 0 || !IsWordChar(sql[index - 1]);
            var afterIndex = index + word.Length;
            var afterOk = afterIndex >= sql.Length || !IsWordChar(sql[afterIndex]);

            if (beforeOk && afterOk)
            {
                return true;
            }

            index = afterIndex;
        }

        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_';
}
