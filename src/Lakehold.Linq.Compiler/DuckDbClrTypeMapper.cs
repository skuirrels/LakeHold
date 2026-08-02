using System.Collections.Concurrent;
using DuckDB.EFCoreProvider.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Lakehold.Linq.Compiler;

/// <summary>Projects the provider's public store-type contract into generated C# property types.</summary>
internal static class DuckDbClrTypeMapper
{
    private static readonly DbContextOptions<MappingContext> MappingOptions =
        new DbContextOptionsBuilder<MappingContext>()
            .UseDuckDB("Data Source=:memory:")
            .Options;
    private static readonly ConcurrentDictionary<string, DuckDBStoreTypeMappingInfo> Mappings =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool TryMap(string storeType, bool nullable, out string? clrType)
    {
        try
        {
            clrType = Map(storeType, nullable);
            return true;
        }
        catch (NotSupportedException)
        {
            clrType = null;
            return false;
        }
    }

    public static string Map(string storeType, bool nullable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeType);
        var mapping = Mappings.GetOrAdd(storeType.Trim(), Inspect);
        if (mapping.Support != DuckDBStoreTypeSupport.ScalarProperty || mapping.ClrType is null)
        {
            throw new NotSupportedException(mapping.Support switch
            {
                DuckDBStoreTypeSupport.ComplexProperty =>
                    $"DuckDB type '{mapping.StoreType}' requires an EF complex-property model and cannot be generated as a scalar LINQ row property.",
                DuckDBStoreTypeSupport.RawReaderOnly =>
                    $"DuckDB type '{mapping.StoreType}' is available to raw readers but not as an EF entity property.",
                _ => $"DuckDB type '{mapping.StoreType}' is not supported by the provider's model contract.",
            });
        }

        var clrType = Render(mapping.ClrType);
        return nullable && Nullable.GetUnderlyingType(mapping.ClrType) is null
            ? string.Concat(clrType, "?")
            : clrType;
    }

    internal static DuckDBStoreTypeMappingInfo Inspect(string storeType)
    {
        using var context = new MappingContext(MappingOptions);
        return context.Database.GetDuckDBStoreTypeMapping(storeType);
    }

    private static string Render(Type type)
    {
        if (type.IsArray)
        {
            return string.Concat(Render(type.GetElementType()!), "[]");
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var name = (definition.FullName ?? definition.Name).Split('`')[0].Replace('+', '.');
            return $"global::{name}<{string.Join(", ", type.GetGenericArguments().Select(Render))}>";
        }

        return type == typeof(bool) ? "bool"
            : type == typeof(byte) ? "byte"
            : type == typeof(sbyte) ? "sbyte"
            : type == typeof(short) ? "short"
            : type == typeof(ushort) ? "ushort"
            : type == typeof(int) ? "int"
            : type == typeof(uint) ? "uint"
            : type == typeof(long) ? "long"
            : type == typeof(ulong) ? "ulong"
            : type == typeof(float) ? "float"
            : type == typeof(double) ? "double"
            : type == typeof(decimal) ? "decimal"
            : type == typeof(string) ? "string"
            : type.FullName is { } fullName ? string.Concat("global::", fullName.Replace('+', '.'))
            : type.Name;
    }

    private sealed class MappingContext(DbContextOptions<MappingContext> options) : DbContext(options);
}
