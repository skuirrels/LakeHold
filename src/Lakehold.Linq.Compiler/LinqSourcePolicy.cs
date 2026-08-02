using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Lakehold.Querying;

namespace Lakehold.Linq.Compiler;

internal static class LinqSourcePolicy
{
    private static readonly SymbolDisplayFormat FullTypeNameFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    private static readonly HashSet<string> AllowedFrameworkTypes = new(StringComparer.Ordinal)
    {
        "System.Boolean", "System.Byte", "System.SByte", "System.Int16", "System.UInt16",
        "System.Int32", "System.UInt32", "System.Int64", "System.UInt64", "System.Single",
        "System.Double", "System.Decimal", "System.String", "System.Guid", "System.DateOnly",
        "System.TimeOnly", "System.DateTime", "System.DateTimeOffset", "System.TimeSpan",
        "System.Math", "System.MathF", "System.Numerics.BigInteger", "System.Linq.Queryable",
    };

    public static IReadOnlyList<QueryDiagnostic> Validate(
        SyntaxTree tree,
        SemanticModel semanticModel,
        GeneratedQuerySource generated,
        int maxArrayElements)
    {
        var diagnostics = new List<QueryDiagnostic>();
        var sourceSpan = new TextSpan(generated.UserSourceStart, generated.Text.Length - generated.UserSourceStart);
        var root = tree.GetRoot();

        foreach (var node in root.DescendantNodes(sourceSpan).Where(node => sourceSpan.Contains(node.Span)))
        {
            switch (node)
            {
                case AssignmentExpressionSyntax:
                case ObjectCreationExpressionSyntax:
                case ImplicitObjectCreationExpressionSyntax:
                case ArrayCreationExpressionSyntax explicitArray when !IsBoundedInitializer(explicitArray, maxArrayElements):
                case ImplicitArrayCreationExpressionSyntax implicitArray
                    when CountInitializerValues(implicitArray.Initializer) > maxArrayElements:
                case AwaitExpressionSyntax:
                case ThrowExpressionSyntax:
                case StackAllocArrayCreationExpressionSyntax:
                case ImplicitStackAllocArrayCreationExpressionSyntax:
                case TypeOfExpressionSyntax:
                    diagnostics.Add(PolicyDiagnostic(node, generated, "LINQ001", "Only a side-effect-free LINQ query expression is allowed."));
                    break;

                case InvocationExpressionSyntax invocation:
                    ValidateInvocation(invocation, semanticModel, generated, diagnostics);
                    break;

                case MemberAccessExpressionSyntax member:
                    ValidateStaticMember(member, semanticModel, generated, diagnostics);
                    break;
            }
        }

        return diagnostics
            .DistinctBy(diagnostic => (diagnostic.Code, diagnostic.StartLine, diagnostic.StartColumn, diagnostic.Message))
            .ToArray();
    }

    private static void ValidateInvocation(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        GeneratedQuerySource generated,
        List<QueryDiagnostic> diagnostics)
    {
        var symbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (symbol is null)
        {
            return;
        }

        var method = symbol.ReducedFrom ?? symbol;
        var typeName = FullTypeName(method.ContainingType);
        var allowed = typeName == "System.Linq.Queryable"
            || typeName == "System.Linq.Enumerable" && method.Name is
                "Sum" or "Count" or "LongCount" or "Average" or "Min" or "Max" or "Any" or "All" or "Contains"
            || typeName is "System.Math" or "System.MathF"
            || typeName == "System.String"
            || typeName is "System.DateOnly" or "System.TimeOnly" or "System.DateTime" or "System.DateTimeOffset"
            || typeName == "DuckDB.EFCoreProvider.Extensions.DuckLakeQueryableExtensions"
                && method.Name is "AsOfSnapshot" or "AsOfTimestamp";

        if (!allowed)
        {
            diagnostics.Add(PolicyDiagnostic(
                invocation,
                generated,
                "LINQ002",
                $"Method '{method.ToDisplayString()}' is outside the read-only LINQ query surface."));
        }
    }

    private static void ValidateStaticMember(
        MemberAccessExpressionSyntax member,
        SemanticModel semanticModel,
        GeneratedQuerySource generated,
        List<QueryDiagnostic> diagnostics)
    {
        var symbol = semanticModel.GetSymbolInfo(member).Symbol;
        if (symbol is not IPropertySymbol and not IFieldSymbol)
        {
            return;
        }

        var isStatic = symbol switch
        {
            IPropertySymbol property => property.IsStatic,
            IFieldSymbol field => field.IsStatic,
            _ => false,
        };
        if (!isStatic)
        {
            return;
        }

        var containingType = symbol.ContainingType is null ? null : FullTypeName(symbol.ContainingType);
        if (containingType is not null && !AllowedFrameworkTypes.Contains(containingType))
        {
            diagnostics.Add(PolicyDiagnostic(
                member,
                generated,
                "LINQ003",
                $"Static member '{symbol.ToDisplayString()}' is outside the read-only LINQ query surface."));
        }
    }

    private static QueryDiagnostic PolicyDiagnostic(
        SyntaxNode node,
        GeneratedQuerySource generated,
        string code,
        string message)
    {
        var span = node.GetLocation().GetLineSpan().Span;
        return new QueryDiagnostic(
            "error",
            code,
            message,
            Math.Max(1, span.Start.Line + 1 - generated.UserSourceLine + 1),
            span.Start.Character + 1,
            Math.Max(1, span.End.Line + 1 - generated.UserSourceLine + 1),
            span.End.Character + 1);
    }

    private static bool IsBoundedInitializer(ArrayCreationExpressionSyntax array, int maxArrayElements)
        => array.Initializer is not null
            && array.Type.RankSpecifiers.SelectMany(rank => rank.Sizes)
                .All(size => size.IsKind(SyntaxKind.OmittedArraySizeExpression))
            && CountInitializerValues(array.Initializer) <= maxArrayElements;

    private static int CountInitializerValues(InitializerExpressionSyntax initializer)
        => initializer.Expressions.Sum(expression => expression is InitializerExpressionSyntax nested
            ? CountInitializerValues(nested)
            : 1);

    private static string FullTypeName(INamedTypeSymbol type)
        => type.ToDisplayString(FullTypeNameFormat);
}
