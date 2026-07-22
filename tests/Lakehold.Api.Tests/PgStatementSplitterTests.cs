using Lakehold.Api.PgWire;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Cover for splitting a simple-query message into statements.
/// </summary>
/// <remarks>
///     A semicolon inside a literal or a comment is the whole risk here: splitting on it produces two
///     fragments that are each invalid SQL, and the client sees a syntax error for a statement it
///     never sent. These cases are cheap to assert and impossible to diagnose from the wire.
/// </remarks>
public sealed class PgStatementSplitterTests
{
    [Fact]
    public void Single_statement_is_returned_whole()
        => Assert.Equal(["SELECT 1"], PgStatementSplitter.Split("SELECT 1"));

    [Fact]
    public void Trailing_semicolon_does_not_produce_an_empty_statement()
        => Assert.Equal(["SELECT 1"], PgStatementSplitter.Split("SELECT 1;"));

    [Fact]
    public void Multiple_statements_split_in_order()
        => Assert.Equal(
            ["SELECT 1", "SELECT 2", "SELECT 3"],
            PgStatementSplitter.Split("SELECT 1; SELECT 2;\nSELECT 3"));

    [Fact]
    public void Empty_and_whitespace_only_bodies_yield_nothing()
    {
        Assert.Empty(PgStatementSplitter.Split(string.Empty));
        Assert.Empty(PgStatementSplitter.Split("  \n ; ;  "));
    }

    [Fact]
    public void Semicolon_inside_a_string_literal_is_not_a_separator()
        => Assert.Equal(
            ["SELECT 'a;b' AS x"],
            PgStatementSplitter.Split("SELECT 'a;b' AS x"));

    [Fact]
    public void Doubled_quote_is_an_escape_not_a_terminator()
        => Assert.Equal(
            ["SELECT 'it''s; fine' AS x"],
            PgStatementSplitter.Split("SELECT 'it''s; fine' AS x"));

    [Fact]
    public void Semicolon_inside_a_quoted_identifier_is_not_a_separator()
        => Assert.Equal(
            ["SELECT 1 AS \"odd;name\""],
            PgStatementSplitter.Split("SELECT 1 AS \"odd;name\""));

    [Fact]
    public void Semicolon_inside_a_line_comment_is_not_a_separator()
        => Assert.Equal(
            ["SELECT 1 -- trailing; comment\n"],
            PgStatementSplitter.Split("SELECT 1 -- trailing; comment\n").Select(s => s + "\n"));

    [Fact]
    public void Semicolon_inside_a_block_comment_is_not_a_separator()
        => Assert.Equal(
            ["SELECT /* a; b */ 1"],
            PgStatementSplitter.Split("SELECT /* a; b */ 1"));

    [Fact]
    public void Dollar_quoted_body_is_opaque()
        => Assert.Equal(
            ["SELECT $tag$ a; b $tag$"],
            PgStatementSplitter.Split("SELECT $tag$ a; b $tag$"));

    /// <summary>
    ///     The shape that actually arrives: Npgsql's type-catalogue load, with comments carrying
    ///     semicolons and several statements in one message.
    /// </summary>
    [Fact]
    public void The_client_handshake_shape_splits_into_its_statements()
    {
        const string Body = """
            SELECT version();

            -- Load all supported types; arrays included
            SELECT ns.nspname, t.oid FROM pg_type AS t
            JOIN pg_namespace AS ns ON ns.oid = t.typnamespace;

            /* Load enum fields; ordered */
            SELECT typ.oid, enumlabel FROM pg_enum
            """;

        var statements = PgStatementSplitter.Split(Body);

        Assert.Equal(3, statements.Count);
        Assert.StartsWith("SELECT version()", statements[0], StringComparison.Ordinal);
        Assert.Contains("pg_namespace", statements[1], StringComparison.Ordinal);
        Assert.Contains("enumlabel", statements[2], StringComparison.Ordinal);
    }
}
