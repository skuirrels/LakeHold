using System.Reflection;
using System.Runtime.Loader;
using DuckDB.EFCoreProvider.Extensions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Lakehold.Querying;

namespace Lakehold.Linq.Compiler;

/// <summary>Compiles one restricted C# LINQ expression into provider-generated DuckDB SQL.</summary>
public sealed class LinqQueryCompiler(IOptions<LinqCompilerOptions> options)
{
    private readonly LinqCompilerOptions _options = options.Value;
    private static readonly Lazy<PortableExecutableReference[]> References = new(CreateReferences);

    public QueryLanguageStarter CreateStarter(QueryCatalogSchema request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SchemaFingerprint);
        ValidateCatalog(request.Tables, nameof(request));

        try
        {
            return new QueryLanguageStarter(
                LinqQuerySourceGenerator.CreateStarter(request.Tables),
                request.SchemaFingerprint);
        }
        catch (NotSupportedException ex)
        {
            throw new LinqPlanningException([
                new QueryDiagnostic("error", "LINQ004", ex.Message, 1, 1, 1, 1),
            ]);
        }
    }

    public async Task<QueryPlan> CompileAsync(
        QueryPlanningRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);

        GeneratedQuerySource generated;
        try
        {
            generated = LinqQuerySourceGenerator.Generate(request);
        }
        catch (NotSupportedException ex)
        {
            throw new LinqPlanningException([
                new QueryDiagnostic("error", "LINQ004", ex.Message, 1, 1, 1, 1),
            ]);
        }

        var tree = CSharpSyntaxTree.ParseText(
            generated.Text,
            new CSharpParseOptions(LanguageVersion.Latest),
            cancellationToken: cancellationToken);
        var compilation = CSharpCompilation.Create(
            string.Concat("Lakehold.Linq.Query.", Guid.NewGuid().ToString("N")),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: false));

        var compilerDiagnostics = compilation.GetDiagnostics(cancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => ToDiagnostic(diagnostic, generated))
            .ToArray();
        if (compilerDiagnostics.Length > 0)
        {
            throw new LinqPlanningException(compilerDiagnostics);
        }

        var policyDiagnostics = LinqSourcePolicy.Validate(
            tree,
            compilation.GetSemanticModel(tree),
            generated,
            _options.MaxArrayElements);
        if (policyDiagnostics.Count > 0)
        {
            throw new LinqPlanningException(policyDiagnostics);
        }

        if (generated.TerminalOperation == LinqTerminalOperation.Query)
        {
            var model = compilation.GetSemanticModel(tree);
            var expression = tree.GetRoot(cancellationToken).FindNode(
                new TextSpan(generated.UserSourceStart, generated.UserSourceLength),
                getInnermostNodeForTie: true);
            var type = model.GetTypeInfo(expression, cancellationToken).Type;
            var queryableType = compilation.GetTypeByMetadataName("System.Linq.IQueryable");
            var genericQueryableType = compilation.GetTypeByMetadataName("System.Linq.IQueryable`1");
            var queryable = type is not null && IsQueryable(type, queryableType, genericQueryableType);
            if (!queryable)
            {
                throw new LinqPlanningException([
                    new QueryDiagnostic(
                        "error",
                        "LINQ007",
                        "This terminal operation has no non-executing DuckDB command-plan API.",
                        1,
                        1,
                        1,
                        Math.Max(1, generated.UserSourceLength)),
                ]);
            }
        }

        await using var assemblyStream = new MemoryStream();
        var emit = compilation.Emit(assemblyStream, cancellationToken: cancellationToken);
        if (!emit.Success)
        {
            throw new LinqPlanningException(
                emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                    .Select(diagnostic => ToDiagnostic(diagnostic, generated)).ToArray());
        }

        assemblyStream.Position = 0;
        var loadContext = new PlannerLoadContext();
        try
        {
            var assembly = loadContext.LoadFromStream(assemblyStream);
            return CapturePlan(assembly, request.SchemaFingerprint, generated.TerminalOperation);
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static bool IsQueryable(
        ITypeSymbol type,
        INamedTypeSymbol? queryableType,
        INamedTypeSymbol? genericQueryableType)
    {
        static bool Matches(
            ITypeSymbol candidate,
            INamedTypeSymbol? queryable,
            INamedTypeSymbol? genericQueryable)
            => SymbolEqualityComparer.Default.Equals(candidate, queryable)
                || candidate is INamedTypeSymbol named
                    && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, genericQueryable);

        return Matches(type, queryableType, genericQueryableType)
            || type.AllInterfaces.Any(candidate => Matches(candidate, queryableType, genericQueryableType));
    }

    private static QueryPlan CapturePlan(
        Assembly assembly,
        string schemaFingerprint,
        LinqTerminalOperation terminalOperation)
    {
        var options = new DbContextOptionsBuilder()
            .UseDuckDB("Data Source=:memory:")
            .Options;
        var contextType = assembly.GetType("Lakehold.Linq.Generated.QueryContext", throwOnError: true)!;
        using var context = (DbContext)Activator.CreateInstance(contextType, options)!;
        var build = assembly.GetType("Lakehold.Linq.Generated.QueryProgram", throwOnError: true)!
            .GetMethod("Build", BindingFlags.Public | BindingFlags.Static)!;

        try
        {
            var result = build.Invoke(null, [context]);
            if (result is IQueryable query)
            {
                var methodName = terminalOperation switch
                {
                    LinqTerminalOperation.Count => nameof(DuckDBDatabaseFacadeExtensions.GetDuckDBCountCommandPlan),
                    LinqTerminalOperation.LongCount => nameof(DuckDBDatabaseFacadeExtensions.GetDuckDBLongCountCommandPlan),
                    LinqTerminalOperation.Any => nameof(DuckDBDatabaseFacadeExtensions.GetDuckDBAnyCommandPlan),
                    LinqTerminalOperation.Min => nameof(DuckDBDatabaseFacadeExtensions.GetDuckDBMinCommandPlan),
                    LinqTerminalOperation.Max => nameof(DuckDBDatabaseFacadeExtensions.GetDuckDBMaxCommandPlan),
                    LinqTerminalOperation.Sum => nameof(DuckDBDatabaseFacadeExtensions.GetDuckDBSumCommandPlan),
                    LinqTerminalOperation.Average => nameof(DuckDBDatabaseFacadeExtensions.GetDuckDBAverageCommandPlan),
                    _ => nameof(DuckDBDatabaseFacadeExtensions.GetDuckDBCommandPlan),
                };
                var method = typeof(DuckDBDatabaseFacadeExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Single(candidate => candidate.Name == methodName && candidate.IsGenericMethodDefinition);
                var providerPlan = (DuckDBCommandPlan)method.MakeGenericMethod(query.ElementType)
                    .Invoke(null, [context.Database, query])!;
                return QueryPlanAdapter.FromProviderPlan(providerPlan, schemaFingerprint);
            }

            throw new InvalidOperationException("The expression did not produce a database query.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw TranslationFailure(ex.InnerException);
        }
        catch (InvalidOperationException ex)
        {
            throw TranslationFailure(ex);
        }
    }

    private static LinqPlanningException TranslationFailure(Exception exception)
        => new([
            new QueryDiagnostic(
                "error",
                "LINQ006",
                $"DuckDB.EFCoreProvider could not translate the expression: {exception.Message}",
                1,
                1,
                1,
                1),
        ]);

    private void ValidateRequest(QueryPlanningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SchemaFingerprint);
        if (request.Source.Length > _options.MaxSourceLength)
        {
            throw new ArgumentException($"LINQ source exceeds the {_options.MaxSourceLength} character limit.", nameof(request));
        }

        ValidateCatalog(request.Tables, nameof(request));

        var expression = SyntaxFactory.ParseExpression(
            request.Source,
            options: new CSharpParseOptions(LanguageVersion.Latest),
            consumeFullText: true);
        var syntaxErrors = expression.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => new QueryDiagnostic(
                "error",
                "LINQ005",
                "The LINQ source must be exactly one C# expression: " + diagnostic.GetMessage(),
                diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1,
                diagnostic.Location.GetLineSpan().StartLinePosition.Character + 1,
                diagnostic.Location.GetLineSpan().EndLinePosition.Line + 1,
                diagnostic.Location.GetLineSpan().EndLinePosition.Character + 1))
            .ToArray();
        if (syntaxErrors.Length > 0)
        {
            throw new LinqPlanningException(syntaxErrors);
        }
    }

    private void ValidateCatalog(IReadOnlyList<QueryTableSchema> tables, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(tables);
        if (tables.Count > _options.MaxTables || tables.Sum(table => table.Columns.Count) > _options.MaxColumns)
        {
            throw new ArgumentException(
                "The catalog schema exceeds the configured LINQ compiler limit.",
                parameterName);
        }
    }

    private static QueryDiagnostic ToDiagnostic(Diagnostic diagnostic, GeneratedQuerySource generated)
    {
        var span = diagnostic.Location.GetLineSpan().Span;
        return new QueryDiagnostic(
            diagnostic.Severity.ToString().ToLowerInvariant(),
            diagnostic.Id,
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
            Math.Max(1, span.Start.Line + 1 - generated.UserSourceLine + 1),
            span.Start.Character + 1,
            Math.Max(1, span.End.Line + 1 - generated.UserSourceLine + 1),
            span.End.Character + 1);
    }

    private static PortableExecutableReference[] CreateReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("The runtime did not expose trusted platform assemblies.");
        var allowedPlatformAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.Private.CoreLib.dll",
            "System.Runtime.dll",
            "netstandard.dll",
            "System.Collections.dll",
            "System.Collections.Concurrent.dll",
            "System.Linq.dll",
            "System.Linq.Queryable.dll",
            "System.Linq.Expressions.dll",
            "System.Runtime.Numerics.dll",
            "System.ComponentModel.dll",
            "System.ComponentModel.Primitives.dll",
            "System.ComponentModel.TypeConverter.dll",
        };
        var entityFrameworkDirectory = Path.GetDirectoryName(typeof(DbContext).Assembly.Location)
            ?? throw new InvalidOperationException("The Entity Framework Core assembly location is unavailable.");
        var paths = trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => allowedPlatformAssemblies.Contains(Path.GetFileName(path)))
            .Append(typeof(DbContext).Assembly.Location)
            .Append(Path.Combine(entityFrameworkDirectory, "Microsoft.EntityFrameworkCore.Relational.dll"))
            .Append(typeof(DuckDBDatabaseFacadeExtensions).Assembly.Location)
            .Distinct(StringComparer.Ordinal);
        return paths.Select(path => MetadataReference.CreateFromFile(path)).ToArray();
    }

    private sealed class PlannerLoadContext() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }
}

/// <summary>A source failure containing editor-ready diagnostics.</summary>
public sealed class LinqPlanningException(IReadOnlyList<QueryDiagnostic> diagnostics)
    : Exception(string.Join(Environment.NewLine, diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")))
{
    public IReadOnlyList<QueryDiagnostic> Diagnostics { get; } = diagnostics;
}
