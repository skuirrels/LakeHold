namespace Lakehold.Api.PgWire;

/// <summary>A response served without reaching the engine.</summary>
/// <param name="Columns">Column names, empty for a statement that returns no rows.</param>
/// <param name="Rows">Row values, aligned to <paramref name="Columns"/>.</param>
/// <param name="Tag">The <c>CommandComplete</c> tag, used when there are no columns.</param>
internal sealed record CannedResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<string?[]> Rows,
    string Tag)
{
    public static CannedResult Acknowledge(string tag) => new([], [], tag);

    public static CannedResult SingleValue(string column, string value) => new([column], [[value]], "SELECT 1");
}

/// <summary>
///     Answers the handful of statements a Postgres client issues that DuckDB has no equivalent for.
/// </summary>
/// <remarks>
///     <para>
///         DuckDB 1.5.4 implements enough of <c>pg_catalog</c> — <c>pg_type</c>, <c>pg_class</c>,
///         <c>pg_namespace</c>, and <c>information_schema</c> — that introspection is passed straight
///         through to the engine. This shim exists only for what remains: session-level statements
///         that are meaningless against a pooled session, and the version string drivers parse to
///         decide which features they may use.
///     </para>
///     <para>
///         Every entry here is a compatibility gap rather than a design decision, so the list is kept
///         short deliberately. Anything that DuckDB can answer should reach DuckDB — a shim that
///         grows to intercept real queries becomes a second, worse query engine.
///     </para>
/// </remarks>
internal static class PgCatalogShim
{
    /// <summary>
    ///     What a client is told when it asks the server's version. Reporting DuckDB's version here
    ///     makes drivers either fail to parse it or fall back to their most conservative behaviour.
    /// </summary>
    private const string VersionString =
        "PostgreSQL 15.0 (Lakehold, DuckDB + DuckLake) on x86_64-pc-linux-gnu, 64-bit";

    public static bool TryAnswer(string sql, out CannedResult result)
    {
        var normalised = Normalise(sql);

        // Session-scoped statements. A wire connection resolves a fresh session per statement, so
        // there is no session for these to affect — acknowledging is both honest about the outcome
        // and the only answer that lets a client proceed.
        //
        // Transaction control is acknowledged for the same reason and is the one to be wary of: a
        // client is told BEGIN succeeded when nothing was begun. It is safe only because every
        // statement a BI tool sends is a read, and DuckLake serialises writes through the session
        // gate regardless. A write client wanting real transactions needs the HTTP path.
        if (normalised.StartsWith("SET ", StringComparison.Ordinal))
        {
            result = CannedResult.Acknowledge("SET");
            return true;
        }

        switch (normalised)
        {
            case "BEGIN" or "START TRANSACTION" or "BEGIN TRANSACTION" or "BEGIN READ ONLY":
                result = CannedResult.Acknowledge("BEGIN");
                return true;

            case "COMMIT" or "END" or "COMMIT TRANSACTION":
                result = CannedResult.Acknowledge("COMMIT");
                return true;

            case "ROLLBACK" or "ABORT" or "ROLLBACK TRANSACTION":
                result = CannedResult.Acknowledge("ROLLBACK");
                return true;

            case "DISCARD ALL":
                result = CannedResult.Acknowledge("DISCARD ALL");
                return true;

            case "SHOW TRANSACTION ISOLATION LEVEL":
                result = CannedResult.SingleValue("transaction_isolation", "read committed");
                return true;

            case "SELECT VERSION()" or "SHOW SERVER_VERSION":
                result = CannedResult.SingleValue("version", VersionString);
                return true;

            default:
                result = null!;
                return false;
        }
    }

    /// <summary>
    ///     Upper-cases and collapses whitespace so a client's formatting does not decide whether a
    ///     statement is recognised.
    /// </summary>
    private static string Normalise(string sql)
    {
        Span<char> buffer = sql.Length <= 256 ? stackalloc char[sql.Length] : new char[sql.Length];
        var written = 0;
        var lastWasSpace = false;

        foreach (var c in sql)
        {
            if (char.IsWhiteSpace(c))
            {
                if (written > 0 && !lastWasSpace)
                {
                    buffer[written++] = ' ';
                    lastWasSpace = true;
                }

                continue;
            }

            buffer[written++] = char.ToUpperInvariant(c);
            lastWasSpace = false;
        }

        while (written > 0 && buffer[written - 1] == ' ')
        {
            written--;
        }

        return new string(buffer[..written]);
    }
}
