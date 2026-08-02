using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Lakehold.Querying;

namespace Lakehold.Linq.Compiler;

internal enum LinqTerminalOperation
{
    Query,
    Count,
    LongCount,
    Any,
    Min,
    Max,
    Sum,
    Average,
}

internal sealed record GeneratedQuerySource(
    string Text,
    int UserSourceStart,
    int UserSourceLength,
    int UserSourceLine,
    LinqTerminalOperation TerminalOperation);

internal static class LinqQuerySourceGenerator
{
    public static GeneratedQuerySource Generate(QueryPlanningRequest request)
    {
        var (source, terminalOperation) = DeferSupportedTerminal(request.Source);
        var tables = SupportedTables(request.Tables);
        EnsureQueryableCatalog(request.Tables, tables);
        var schemas = UniqueNames(
            tables.Select(table => table.Definition.Schema).Distinct(StringComparer.Ordinal),
            value => CSharpIdentifier.Pascal(value, "Schema"));
        var tableTypeNames = UniqueNames(
            tables,
            table => CSharpIdentifier.Pascal(
                string.Concat(table.Definition.Schema, "_", table.Definition.Name, "_Row"),
                "TableRow"));
        var tablePropertyNames = tables
            .GroupBy(table => table.Definition.Schema, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => UniqueNames(group, table => CSharpIdentifier.Pascal(table.Definition.Name, "Table")),
                StringComparer.Ordinal);

        var builder = new StringBuilder(
            """
            #nullable enable
            using System;
            using System.Linq;
            using System.Numerics;
            using Microsoft.EntityFrameworkCore;

            namespace Lakehold.Linq.Generated;

            public sealed class QueryContext(DbContextOptions options) : DbContext(options)
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
            """);
        builder.AppendLine();

        var entityIndex = 0;
        foreach (var table in tables)
        {
            var typeName = tableTypeNames[table];
            var definition = table.Definition;
            var variable = string.Concat("entity", entityIndex++);
            builder.Append("        var ").Append(variable)
                .Append(" = modelBuilder.Entity<").Append(typeName).AppendLine(">();");
            builder.Append("        ").Append(variable).AppendLine(".HasNoKey();");
            builder.Append("        ").Append(variable)
                .Append(string.Equals(definition.Kind, "view", StringComparison.OrdinalIgnoreCase) ? ".ToView(" : ".ToTable(")
                .Append(Literal(definition.Name)).Append(", ").Append(Literal(definition.Schema)).AppendLine(");");

            var properties = ColumnNames(table);
            foreach (var column in table.Columns)
            {
                var property = properties[column];
                builder.Append("        ").Append(variable).Append(".Property(row => row.")
                    .Append(property).Append(").HasColumnName(").Append(Literal(column.Name))
                    .Append(").HasColumnType(").Append(Literal(column.StoreType)).AppendLine(");");
            }
        }

        builder.AppendLine("    }");
        builder.AppendLine("}");

        foreach (var table in tables)
        {
            builder.Append("public sealed class ").Append(tableTypeNames[table]).AppendLine();
            builder.AppendLine("{");
            var properties = ColumnNames(table);
            foreach (var column in table.Columns)
            {
                builder.Append("    public ").Append(DuckDbClrTypeMapper.Map(column.StoreType, column.IsNullable))
                    .Append(' ').Append(properties[column]).AppendLine(" { get; set; }");
            }

            builder.AppendLine("}");
        }

        foreach (var schema in tables.GroupBy(table => table.Definition.Schema, StringComparer.Ordinal))
        {
            var schemaType = string.Concat(schemas[schema.Key].TrimStart('@'), "Schema");
            builder.Append("public sealed class ").Append(schemaType).AppendLine("(QueryContext context)");
            builder.AppendLine("{");
            foreach (var table in schema)
            {
                builder.Append("    public IQueryable<").Append(tableTypeNames[table]).Append("> ")
                    .Append(tablePropertyNames[schema.Key][table]).Append(" => context.Set<")
                    .Append(tableTypeNames[table]).AppendLine(">().AsNoTracking();");
            }

            builder.AppendLine("}");
        }

        builder.AppendLine("public static class QueryProgram");
        builder.AppendLine("{");
        builder.AppendLine("    public static object? Build(QueryContext context)");
        builder.AppendLine("    {");
        foreach (var schema in tables.Select(table => table.Definition.Schema).Distinct(StringComparer.Ordinal))
        {
            var identifier = schemas[schema];
            builder.Append("        var ").Append(identifier).Append(" = new ")
                .Append(identifier.TrimStart('@')).AppendLine("Schema(context);");
        }

        builder.Append("        return (");
        var start = builder.Length;
        var line = CountLines(builder);
        builder.Append(source);
        builder.AppendLine(");");
        builder.AppendLine("    }");
        builder.AppendLine("}");

        return new GeneratedQuerySource(builder.ToString(), start, source.Length, line, terminalOperation);
    }

    public static string CreateStarter(IReadOnlyList<QueryTableSchema> catalogTables)
    {
        var tables = SupportedTables(catalogTables);
        EnsureQueryableCatalog(catalogTables, tables);
        if (tables.Length == 0)
        {
            throw new NotSupportedException("The catalog has no queryable tables for a LINQ starter.");
        }

        var schemas = UniqueNames(
            tables.Select(table => table.Definition.Schema).Distinct(StringComparer.Ordinal),
            value => CSharpIdentifier.Pascal(value, "Schema"));
        var tablePropertyNames = tables
            .GroupBy(table => table.Definition.Schema, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => UniqueNames(group, table => CSharpIdentifier.Pascal(table.Definition.Name, "Table")),
                StringComparer.Ordinal);
        var first = tables[0];

        return $"from row in {schemas[first.Definition.Schema]}.{tablePropertyNames[first.Definition.Schema][first]}\nselect row";
    }

    private static Dictionary<QueryColumnSchema, string> ColumnNames(SupportedTable table)
        => UniqueNames(table.Columns, column => CSharpIdentifier.Pascal(column.Name, "Column"));

    private static SupportedTable[] SupportedTables(IReadOnlyList<QueryTableSchema> tables)
        => tables
            .Select(table => new SupportedTable(
                table,
                [.. table.Columns.Where(column =>
                    DuckDbClrTypeMapper.TryMap(column.StoreType, column.IsNullable, out _))]))
            .Where(table => table.Columns.Count > 0)
            .ToArray();

    private static void EnsureQueryableCatalog(
        IReadOnlyList<QueryTableSchema> requested,
        IReadOnlyList<SupportedTable> supported)
    {
        if (supported.Count > 0 || requested.Count == 0)
        {
            return;
        }

        var firstColumn = requested.SelectMany(table => table.Columns).FirstOrDefault();
        if (firstColumn is not null)
        {
            _ = DuckDbClrTypeMapper.Map(firstColumn.StoreType, firstColumn.IsNullable);
        }

        throw new NotSupportedException("The catalog has no columns that can be mapped into the LINQ model.");
    }

    private static Dictionary<T, string> UniqueNames<T>(IEnumerable<T> values, Func<T, string> select)
        where T : notnull
    {
        var result = new Dictionary<T, string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var candidate = select(value);
            var suffix = 2;
            while (!used.Add(candidate))
            {
                candidate = string.Concat(select(value), "_", suffix++);
            }

            result.Add(value, candidate);
        }

        return result;
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    private static (string Source, LinqTerminalOperation Operation) DeferSupportedTerminal(string source)
    {
        var expression = SyntaxFactory.ParseExpression(source, consumeFullText: true);
        if (expression is not InvocationExpressionSyntax invocation
            || invocation.Expression is not MemberAccessExpressionSyntax member)
        {
            return (source, LinqTerminalOperation.Query);
        }

        var operation = member.Name.Identifier.ValueText switch
        {
            "Count" => LinqTerminalOperation.Count,
            "LongCount" => LinqTerminalOperation.LongCount,
            "Any" => LinqTerminalOperation.Any,
            "Min" => LinqTerminalOperation.Min,
            "Max" => LinqTerminalOperation.Max,
            "Sum" => LinqTerminalOperation.Sum,
            "Average" => LinqTerminalOperation.Average,
            _ => LinqTerminalOperation.Query,
        };
        if (operation == LinqTerminalOperation.Query)
        {
            return (source, operation);
        }

        var receiver = member.Expression.ToString();
        var isStaticQueryable = receiver is "Queryable" or "System.Linq.Queryable" or "global::System.Linq.Queryable";
        var arguments = invocation.ArgumentList.Arguments;
        if ((isStaticQueryable && arguments.Count is not (1 or 2))
            || (!isStaticQueryable && arguments.Count > 1))
        {
            return (source, LinqTerminalOperation.Query);
        }

        var query = isStaticQueryable ? arguments[0].Expression.ToFullString() : member.Expression.ToFullString();
        var operationArgumentIndex = isStaticQueryable ? 1 : 0;
        if (arguments.Count > operationArgumentIndex)
        {
            var operatorName = operation is LinqTerminalOperation.Count
                or LinqTerminalOperation.LongCount
                or LinqTerminalOperation.Any
                ? "Where"
                : "Select";
            query = $"({query}).{operatorName}({arguments[operationArgumentIndex].Expression.ToFullString()})";
        }

        return (query, operation);
    }

    private static int CountLines(StringBuilder builder)
    {
        var lines = 1;
        for (var index = 0; index < builder.Length; index++)
        {
            if (builder[index] == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    private sealed record SupportedTable(
        QueryTableSchema Definition,
        IReadOnlyList<QueryColumnSchema> Columns);
}
