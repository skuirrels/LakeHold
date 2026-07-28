using Lakehold.Engine.Execution;

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
    public static IReadOnlyList<string> Split(string sql) => SqlStatementSplitter.Split(sql);
}
