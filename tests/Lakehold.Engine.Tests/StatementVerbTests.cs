using Lakehold.Engine.Execution;
using Xunit;

namespace Lakehold.Engine.Tests;

/// <summary>
///     Cover for the statement classification that chooses between the streaming and non-query
///     paths. Its failure modes are asymmetric: not recognising a statement costs a count, while
///     wrongly recognising one that returns rows would discard them, so the unrecognised cases
///     matter as much as the recognised ones.
/// </summary>
public sealed class StatementVerbTests
{
    [Theory]
    [InlineData("SELECT 1", "SELECT")]
    [InlineData("  \n\t insert into t values (1)", "INSERT")]
    [InlineData("-- a leading comment\nUPDATE t SET a = 1", "UPDATE")]
    [InlineData("/* block */ DELETE FROM t", "DELETE")]
    [InlineData("/* one */ -- two\n  MERGE INTO t", "MERGE")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("(SELECT 1)", "")]
    public void Reads_the_leading_keyword(string sql, string expected)
        => Assert.Equal(expected, StatementVerb.Of(sql));

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("update t set a = 1")]
    [InlineData("DELETE FROM t WHERE id = 1")]
    [InlineData("MERGE INTO t USING s ON t.id = s.id WHEN MATCHED THEN DELETE")]
    [InlineData("-- comment\nINSERT INTO t VALUES (1)")]
    [InlineData("INSERT INTO t VALUES (1, {'a': 1})")]
    public void Counts_dml(string sql) => Assert.True(StatementVerb.ReportsAffectedRows(sql));

    [Theory]
    [InlineData("SELECT * FROM t")]
    [InlineData("CREATE TABLE t (id INTEGER)")]
    [InlineData("CALL ducklake_flush_inlined_data('lake')")]
    [InlineData("COPY t TO 'out.parquet'")]
    // A CTE-led write is not classified: the leading keyword is WITH, so it streams and simply
    // reports no count, which is the safe direction to be wrong in.
    [InlineData("WITH s AS (SELECT 1) INSERT INTO t SELECT * FROM s")]
    // RETURNING makes the rows the result. DuckLake rejects it, but executing it as a non-query
    // would silently swallow a result set, so it is excluded rather than relied upon.
    [InlineData("INSERT INTO t VALUES (1) RETURNING id")]
    [InlineData("delete from t returning *")]
    public void Does_not_count_everything_else(string sql)
        => Assert.False(StatementVerb.ReportsAffectedRows(sql));

    /// <summary>
    ///     The <c>RETURNING</c> search is a whole-word one, so a column or table whose name merely
    ///     contains it still takes the counted path.
    /// </summary>
    [Fact]
    public void Returning_must_be_a_whole_word()
    {
        Assert.True(StatementVerb.ReportsAffectedRows("UPDATE t SET returning_customers = 1"));
        Assert.True(StatementVerb.ReportsAffectedRows("INSERT INTO prereturning VALUES (1)"));
    }

    /// <summary>
    ///     A statement that only reads leaves other sessions' view of the catalog current.
    /// </summary>
    [Theory]
    [InlineData("SELECT * FROM t")]
    [InlineData("  \n-- comment\nselect 1")]
    [InlineData("WITH s AS (SELECT 1) SELECT * FROM s")]
    [InlineData("FROM t SELECT *")]
    [InlineData("DESCRIBE t")]
    [InlineData("SHOW TABLES")]
    [InlineData("EXPLAIN SELECT 1")]
    [InlineData("SUMMARIZE t")]
    public void Reads_do_not_commit(string sql)
        => Assert.False(StatementVerb.MayCommit(sql));

    /// <summary>
    ///     Anything that can advance the catalog does, including the shapes whose *leading* keyword
    ///     reads like a query.
    /// </summary>
    /// <remarks>
    ///     The CTE-prefixed cases are the ones worth pinning. DuckDB accepts a CTE in front of a
    ///     write, so classifying on the leading keyword alone reported them as reads and left another
    ///     session answering from the snapshot it attached at — the stale read this classification
    ///     exists to prevent, still open for a common SQL shape.
    /// </remarks>
    [Theory]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("UPDATE t SET i = 1")]
    [InlineData("DELETE FROM t")]
    [InlineData("CREATE TABLE t (i INT)")]
    [InlineData("DROP TABLE t")]
    [InlineData("ALTER TABLE t ADD COLUMN j INT")]
    [InlineData("COPY t TO 'x.parquet'")]
    [InlineData("CALL something()")]
    [InlineData("WITH x AS (SELECT 1 AS i) INSERT INTO t SELECT * FROM x")]
    [InlineData("with new_rows as (select 1) delete from t where id in (select * from new_rows)")]
    [InlineData("WITH x AS (SELECT 1) UPDATE t SET i = 1")]
    [InlineData("SELECT 1; INSERT INTO t VALUES (1)")]
    public void Writes_may_commit(string sql)
        => Assert.True(StatementVerb.MayCommit(sql));

    /// <summary>
    ///     An unrecognised leading keyword is treated as committing.
    /// </summary>
    /// <remarks>
    ///     The opposite of the fall-through <c>ReportsAffectedRows</c> takes, and deliberately so:
    ///     there, being wrong costs a missing row count; here, being wrong serves a caller data from
    ///     before their own write.
    /// </remarks>
    [Theory]
    [InlineData("VACUUM")]
    [InlineData("CHECKPOINT")]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unknown_statement_is_assumed_to_commit(string sql)
        => Assert.True(StatementVerb.MayCommit(sql));
}
