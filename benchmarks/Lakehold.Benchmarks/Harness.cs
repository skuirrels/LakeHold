using System.Diagnostics;

namespace Lakehold.Benchmarks;

/// <summary>Result of one measured workload: median per-op time and per-op managed allocation.</summary>
public sealed record Sample(string Name, double NanosecondsPerOp, long BytesPerOp);

/// <summary>
///     A small, dependency-free measurement harness.
/// </summary>
/// <remarks>
///     Not a replacement for BenchmarkDotNet — it does not isolate processes or defeat every JIT
///     effect — but it is enough to compare two implementations of the same operation on one machine:
///     it warms up, times several trials and takes the median to shed outliers, and reads managed
///     allocation directly from <see cref="GC.GetAllocatedBytesForCurrentThread"/>. Allocation is the
///     headline number for most of these changes and it is measured exactly, not sampled.
/// </remarks>
public static class Harness
{
    public static Sample MeasureSync(string name, int opsPerTrial, Action body, int trials = 15, int warmupTrials = 3)
    {
        // Warm up: let the JIT compile the delegate and settle tiered compilation before timing.
        for (var w = 0; w < warmupTrials; w++)
        {
            RunSync(opsPerTrial, body);
        }

        var times = new double[trials];
        for (var t = 0; t < trials; t++)
        {
            times[t] = RunSync(opsPerTrial, body) / opsPerTrial;
        }

        return new Sample(name, Median(times), MeasureAllocationSync(opsPerTrial, body));
    }

    public static async Task<Sample> MeasureAsync(
        string name, int opsPerTrial, Func<Task> body, int trials = 9, int warmupTrials = 2)
    {
        for (var w = 0; w < warmupTrials; w++)
        {
            await RunAsync(opsPerTrial, body).ConfigureAwait(false);
        }

        var times = new double[trials];
        for (var t = 0; t < trials; t++)
        {
            times[t] = await RunAsync(opsPerTrial, body).ConfigureAwait(false) / opsPerTrial;
        }

        return new Sample(name, Median(times), BytesPerOp: -1);
    }

    private static double RunSync(int ops, Action body)
    {
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < ops; i++)
        {
            body();
        }

        sw.Stop();
        return sw.Elapsed.TotalMilliseconds * 1_000_000.0; // ms -> ns for the whole trial
    }

    private static async Task<double> RunAsync(int ops, Func<Task> body)
    {
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < ops; i++)
        {
            await body().ConfigureAwait(false);
        }

        sw.Stop();
        return sw.Elapsed.TotalMilliseconds * 1_000_000.0;
    }

    private static long MeasureAllocationSync(int ops, Action body)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < ops; i++)
        {
            body();
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        return (after - before) / ops;
    }

    private static double Median(double[] values)
    {
        Array.Sort(values);
        var mid = values.Length / 2;
        return values.Length % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
    }

    public static void PrintComparison(string title, string metric, Sample before, Sample after)
    {
        Console.WriteLine();
        Console.WriteLine($"### {title}");
        Console.WriteLine($"| variant | {metric} | bytes/op |");
        Console.WriteLine("|---|--:|--:|");
        Console.WriteLine($"| before | {Fmt(before.NanosecondsPerOp)} | {Bytes(before.BytesPerOp)} |");
        Console.WriteLine($"| after  | {Fmt(after.NanosecondsPerOp)} | {Bytes(after.BytesPerOp)} |");

        var speedup = before.NanosecondsPerOp / after.NanosecondsPerOp;
        Console.Write($"→ time: {speedup:0.00}× ");
        Console.Write(speedup >= 1 ? "faster" : "slower");
        if (before.BytesPerOp >= 0 && after.BytesPerOp >= 0)
        {
            var saved = before.BytesPerOp - after.BytesPerOp;
            var pct = before.BytesPerOp == 0 ? 0 : 100.0 * saved / before.BytesPerOp;
            Console.Write($"; allocation: {saved:N0} B/op saved ({pct:0.0}%)");
        }

        Console.WriteLine();
    }

    private static string Fmt(double ns) => ns >= 1000 ? $"{ns / 1000.0:0.00} µs" : $"{ns:0.0} ns";

    private static string Bytes(long b) => b < 0 ? "n/a" : $"{b:N0}";
}
