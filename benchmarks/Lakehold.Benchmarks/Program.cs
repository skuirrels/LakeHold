using Lakehold.Benchmarks;

// Before/after benchmarks for the performance changes. Run a subset by name, or all by default:
//   dotnet run -c Release --project benchmarks/Lakehold.Benchmarks -- [wire] [sweep] [parquet] [resolve]
var selected = args.Length == 0
    ? ["wire", "sweep", "parquet", "resolve"]
    : args;

Console.WriteLine("# Lakehold performance: before / after");
Console.WriteLine($"_Machine: {Environment.ProcessorCount} logical cores · .NET {Environment.Version} · " +
    $"{(Environment.Is64BitProcess ? "x64" : "x86")} · server-GC {System.Runtime.GCSettings.IsServerGC}_");

foreach (var name in selected)
{
    switch (name.ToLowerInvariant())
    {
        case "wire":
            WireValueBench.Run();
            break;
        case "sweep":
            SweepBench.Run(warmSessions: 32);
            break;
        case "parquet":
            await ParquetCountBench.RunAsync(rowCount: 5_000_000).ConfigureAwait(false);
            break;
        case "resolve":
            await ResolveBench.RunAsync().ConfigureAwait(false);
            break;
        default:
            Console.WriteLine($"unknown benchmark '{name}'");
            break;
    }
}

Console.WriteLine();
Console.WriteLine("_#5 (catalog-explorer filter) is measured separately: `node benchmarks/filter-bench.mjs`._");
Console.WriteLine("_#6 (result-grid @let) is a template-compile change with no runtime workload to sample._");
