using System.Data;

namespace Lakehold.Engine.Execution;

/// <summary>One provider-neutral named ADO.NET parameter for an executable query command.</summary>
public sealed record NamedQueryParameter(
    string Name,
    object? Value,
    DbType DbType,
    bool IsNullable,
    int Size,
    byte Precision,
    byte Scale);
