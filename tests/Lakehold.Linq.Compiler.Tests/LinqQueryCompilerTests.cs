using Microsoft.Extensions.Options;
using System.Data;
using System.Numerics;
using System.Text.Json;
using Lakehold.Linq.Compiler;
using Lakehold.Querying;
using DuckDB.EFCoreProvider.Extensions;
using Xunit;

namespace Lakehold.Linq.Compiler.Tests;

public sealed class LinqQueryCompilerTests
{
    private static readonly int[] IntegerValues = [1, 2, 3];
    private static readonly byte[] ByteValues = [4, 5, 6];
    private readonly LinqQueryCompiler _compiler = new(Options.Create(new LinqCompilerOptions()));

    [Fact]
    public async Task Query_expression_is_translated_without_a_catalog_connection()
    {
        var plan = await _compiler.CompileAsync(Request(
            """
            from e in Main.Events
            where e.EventType == "purchase"
            group e by e.Country into purchases
            orderby purchases.Sum(x => x.Revenue) descending
            select new { Country = purchases.Key, Count = purchases.Count(), Revenue = purchases.Sum(x => x.Revenue) }
            """), default);

        Assert.Contains("events", plan.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GROUP BY", plan.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", plan.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("schema-1", plan.SchemaFingerprint);
    }

    [Fact]
    public async Task Terminal_count_uses_the_provider_command_plan()
    {
        var plan = await _compiler.CompileAsync(Request("Main.Events.Count(e => e.Revenue > 100m)"), default);

        Assert.Contains("count", plan.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("revenue", plan.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Terminal_any_uses_the_provider_command_plan()
    {
        var plan = await _compiler.CompileAsync(Request("Main.Events.Any(e => e.Country == \"GB\")"), default);

        Assert.Contains("EXISTS", plan.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Static_terminal_count_uses_the_provider_command_plan()
    {
        var plan = await _compiler.CompileAsync(
            Request("System.Linq.Queryable.Count(Main.Events, e => e.Country == \"GB\")"),
            default);

        Assert.Contains("count", plan.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Main.Events.LongCount(e => e.Revenue > 100m)", "count")]
    [InlineData("Main.Events.Sum(e => e.Revenue)", "sum")]
    [InlineData("Main.Events.Average(e => e.Revenue)", "avg")]
    [InlineData("Main.Events.Min(e => e.Revenue)", "min")]
    [InlineData("Main.Events.Max(e => e.Revenue)", "max")]
    public async Task Terminal_aggregate_uses_the_provider_command_plan(string source, string sqlFunction)
    {
        var plan = await _compiler.CompileAsync(Request(source), default);

        Assert.Contains(sqlFunction, plan.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Static_terminal_sum_uses_the_provider_command_plan()
    {
        var plan = await _compiler.CompileAsync(
            Request("System.Linq.Queryable.Sum(Main.Events, e => e.Revenue)"),
            default);

        Assert.Contains("sum", plan.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Main.Events.First()")]
    [InlineData("Main.Events.All(e => e.Revenue > 0m)")]
    [InlineData("Main.Events.Select(e => e.Country).Contains(\"GB\")")]
    public async Task Unsupported_terminal_is_rejected_without_executing_the_scratch_context(string source)
    {
        var exception = await Assert.ThrowsAsync<LinqPlanningException>(() =>
            _compiler.CompileAsync(Request(source), default));

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "LINQ007");
    }

    [Fact]
    public async Task Provider_plan_preserves_an_equally_named_column_and_exact_named_parameter()
    {
        var request = new QueryPlanningRequest(
            "Main.Events.Take(10)",
            "schema-1",
            [new QueryTableSchema(
                "main",
                "events",
                "TABLE",
                [new QueryColumnSchema("p", "INTEGER", false)])]);

        var plan = await _compiler.CompileAsync(request, default);

        Assert.Contains("e.p", plan.Sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT $", plan.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", plan.Sql, StringComparison.Ordinal);
        var parameter = Assert.Single(plan.Parameters);
        Assert.NotEmpty(parameter.Name);
        Assert.Equal(DbType.Int32, parameter.DbType);
    }

    [Fact]
    public async Task Process_access_is_refused_by_semantic_policy()
    {
        var exception = await Assert.ThrowsAsync<LinqPlanningException>(() =>
            _compiler.CompileAsync(Request("System.Diagnostics.Process.GetProcesses().AsQueryable()"), default));

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code is "LINQ002" or "CS1069");
    }

    [Fact]
    public async Task Runtime_sized_array_allocation_is_refused_by_semantic_policy()
    {
        var exception = await Assert.ThrowsAsync<LinqPlanningException>(() =>
            _compiler.CompileAsync(
                Request("(new int[10]).Length > 0 ? Main.Events : Main.Events"),
                default));

        Assert.Contains(exception.Diagnostics, diagnostic =>
            diagnostic.Code is "LINQ001" or "CS1069");
    }

    [Fact]
    public async Task Literal_array_allocation_is_bounded_by_compiler_policy()
    {
        var compiler = new LinqQueryCompiler(Options.Create(new LinqCompilerOptions
        {
            MaxArrayElements = 2,
        }));

        var exception = await Assert.ThrowsAsync<LinqPlanningException>(() =>
            compiler.CompileAsync(
                Request("Main.Events.Where(e => new[] { \"GB\", \"US\", \"CA\" }.Contains(e.Country))"),
                default));

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "LINQ001");
    }

    [Fact]
    public async Task Provider_translatable_string_predicates_are_allowed()
    {
        var plan = await _compiler.CompileAsync(
            Request("Main.Events.Where(e => e.Country.Contains(\"GB\"))"),
            default);

        Assert.Contains("contains", plan.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provider_command_extensions_are_refused_by_semantic_policy()
    {
        var exception = await Assert.ThrowsAsync<LinqPlanningException>(() =>
            _compiler.CompileAsync(Request(
                "DuckDB.EFCoreProvider.Extensions.DuckDBDatabaseFacadeExtensions.SqlQueryDynamicRawAsync(context.Database, \"SELECT 1\")"),
                default));

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "LINQ002");
    }

    [Fact]
    public async Task Unsupported_complex_type_is_reported_as_a_source_diagnostic()
    {
        var request = new QueryPlanningRequest(
            "Main.Events",
            "schema-1",
            [new QueryTableSchema("main", "events", "TABLE", [new QueryColumnSchema("payload", "STRUCT(name VARCHAR)", true)])]);

        var exception = await Assert.ThrowsAsync<LinqPlanningException>(() => _compiler.CompileAsync(request, default));

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "LINQ004");
    }

    [Fact]
    public async Task Unsupported_columns_on_other_tables_do_not_disable_supported_query_roots()
    {
        var request = new QueryPlanningRequest(
            "Main.Events.Select(e => e.Country)",
            "schema-1",
            [
                new QueryTableSchema(
                    "main",
                    "events",
                    "TABLE",
                    [
                        new QueryColumnSchema("country", "VARCHAR", false),
                        new QueryColumnSchema("payload", "STRUCT(name VARCHAR)", true),
                    ]),
                new QueryTableSchema(
                    "main",
                    "nested_payloads",
                    "TABLE",
                    [new QueryColumnSchema("payload", "STRUCT(name VARCHAR)", true)]),
            ]);

        var plan = await _compiler.CompileAsync(request, default);

        Assert.Contains("main.events", plan.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalog_starter_uses_the_compilers_canonical_identifiers()
    {
        var starter = _compiler.CreateStarter(new QueryCatalogSchema(
            "schema-1",
            [new QueryTableSchema(
                "123-data",
                "order-items",
                "TABLE",
                [new QueryColumnSchema("line-id", "INTEGER", false)])]));

        Assert.Equal("from row in _123Data.OrderItems\nselect row", starter.Source);
        Assert.Equal("schema-1", starter.SchemaFingerprint);
    }

    [Fact]
    public async Task Source_cannot_escape_the_generated_expression_wrapper()
    {
        var exception = await Assert.ThrowsAsync<LinqPlanningException>(() => _compiler.CompileAsync(
            Request("Main.Events); while (true) { } return (Main.Events"),
            default));

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "LINQ005");
    }

    [Fact]
    public void Planner_parameters_round_trip_provider_compatible_types()
    {
        Assert.Equal(
            IntegerValues,
            Assert.IsType<int[]>(QueryParameterCodec.Decode(new QueryParameter(
                "p0",
                "System.Int32[]",
                JsonSerializer.SerializeToElement(IntegerValues),
                DbType.Object,
                false))));
        Assert.Equal(
            ByteValues,
            Assert.IsType<byte[]>(QueryParameterCodec.Decode(new QueryParameter(
                "p1",
                "System.Byte[]",
                JsonSerializer.SerializeToElement(ByteValues),
                DbType.Binary,
                false))));
        Assert.Equal(
            BigInteger.Parse("123456789012345678901234567890"),
            Assert.IsType<BigInteger>(QueryParameterCodec.Decode(new QueryParameter(
                "p2",
                "System.Numerics.BigInteger",
                JsonSerializer.SerializeToElement("123456789012345678901234567890"),
                DbType.VarNumeric,
                false))));
    }

    [Theory]
    [InlineData("JSON", true, "string?")]
    [InlineData("UUID", false, "global::System.Guid")]
    [InlineData("BLOB", true, "byte[]?")]
    [InlineData("DATE", false, "global::System.DateOnly")]
    [InlineData("TIME", false, "global::System.TimeOnly")]
    [InlineData("TIMESTAMP", false, "global::System.DateTime")]
    [InlineData("TIMESTAMPTZ", false, "global::System.DateTimeOffset")]
    [InlineData("DECIMAL(18, 2)", true, "decimal?")]
    [InlineData("TIMESTAMP_NS", false, "global::System.DateTime")]
    [InlineData("INTEGER[]", true, "global::System.Collections.Generic.List<int>?")]
    [InlineData("VARCHAR[]", false, "global::System.Collections.Generic.List<string>")]
    public void Provider_mapped_store_types_generate_expected_properties(
        string storeType,
        bool nullable,
        string expected)
        => Assert.Equal(expected, DuckDbClrTypeMapper.Map(storeType, nullable));

    [Fact]
    public async Task Provider_inspected_list_mapping_builds_a_queryable_model()
    {
        var request = new QueryPlanningRequest(
            "Main.Events.Select(e => e.Tags)",
            "schema-1",
            [new QueryTableSchema(
                "main",
                "events",
                "TABLE",
                [new QueryColumnSchema("tags", "VARCHAR[]", true)])]);

        var plan = await _compiler.CompileAsync(request, default);

        Assert.Contains("tags", plan.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("HUGEINT")]
    [InlineData("INTERVAL")]
    [InlineData("MAP(VARCHAR, INTEGER)")]
    [InlineData("LIST(VARCHAR)")]
    public void Raw_reader_only_types_are_rejected_at_the_EF_model_boundary(string storeType)
        => Assert.Throws<NotSupportedException>(() => DuckDbClrTypeMapper.Map(storeType, nullable: true));

    [Theory]
    [InlineData("STRUCT(name VARCHAR)", DuckDBStoreTypeSupport.ComplexProperty)]
    [InlineData("HUGEINT", DuckDBStoreTypeSupport.RawReaderOnly)]
    [InlineData("NOT_A_DUCKDB_TYPE", DuckDBStoreTypeSupport.Unsupported)]
    public void Provider_inspection_defines_the_dynamic_model_boundary(
        string storeType,
        DuckDBStoreTypeSupport expected)
        => Assert.Equal(expected, DuckDbClrTypeMapper.Inspect(storeType).Support);

    private static QueryPlanningRequest Request(string source)
        => new(
            source,
            "schema-1",
            [
                new QueryTableSchema(
                    "main",
                    "events",
                    "TABLE",
                    [
                        new QueryColumnSchema("event_type", "VARCHAR", false),
                        new QueryColumnSchema("country", "VARCHAR", false),
                        new QueryColumnSchema("revenue", "DECIMAL(18,2)", false),
                        new QueryColumnSchema("recorded_at", "TIMESTAMP", true),
                    ]),
            ]);
}
