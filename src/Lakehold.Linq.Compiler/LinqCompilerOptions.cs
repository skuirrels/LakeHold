namespace Lakehold.Linq.Compiler;

/// <summary>Resource and authentication limits for the isolated compiler.</summary>
public sealed class LinqCompilerOptions
{
    public const string Section = "Lakehold:LinqCompiler";

    public int MaxSourceLength { get; set; } = 100_000;

    public int MaxTables { get; set; } = 1_000;

    public int MaxColumns { get; set; } = 20_000;

    public int MaxArrayElements { get; set; } = 1_000;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    public int MaxConcurrentCompilations { get; set; } = 1;

    public int MaxQueuedCompilations { get; set; } = 8;

    public long MaxRequestBodyBytes { get; set; } = 2 * 1024 * 1024;

    public string? SharedSecret { get; set; }
}
