using System.Collections;
using System.Globalization;
using System.Numerics;

namespace Lakehold.Benchmarks;

/// <summary>
///     Finding #3: <c>Duckling.ToWireValue</c> — stringify only integers outside JS's safe range.
/// </summary>
/// <remarks>
///     Projects one representative result row (the demo <c>events</c> shape) cell-by-cell, the exact
///     work the streaming path does per row. <see cref="BeforeArm"/> and <see cref="AfterArm"/>
///     mirror the production switch verbatim; the only difference is the integer arm.
/// </remarks>
public static class WireValueBench
{
    private const long MaxSafeInteger = 9_007_199_254_740_991;

    // A raw events row: event_id, occurred_at, customer_id, event_type, revenue, country.
    // Two in-range BIGINTs (the values that used to allocate strings), one DECIMAL and one TIMESTAMP
    // (strings in both versions), two VARCHARs (untouched passthrough in both).
    private static readonly object?[] Row =
    [
        1_234_567L,
        new DateTime(2026, 1, 1, 12, 30, 0, DateTimeKind.Utc),
        4_821L,
        "purchase",
        249.99m,
        "GB",
    ];

    public static void Run()
    {
        const int rows = 2_000_000;

        var before = Harness.MeasureSync("before", rows, () => ProjectRow(Row, BeforeArm));
        var after = Harness.MeasureSync("after", rows, () => ProjectRow(Row, AfterArm));

        Harness.PrintComparison(
            "#3 ToWireValue — per result row (6 cells, demo events shape)",
            "ns/row",
            before,
            after);
    }

    private static void ProjectRow(object?[] row, Func<object?, object?> arm)
    {
        // Mirrors ExecuteUnguardedAsync's inner loop: allocate the projected row and fill each cell.
        var values = new object?[row.Length];
        for (var i = 0; i < row.Length; i++)
        {
            values[i] = arm(row[i]);
        }
    }

    // ---- before: every long/ulong/decimal/BigInteger becomes a string ----
    private static object? BeforeArm(object? value) => value switch
    {
        null => null,
        decimal or long or ulong or BigInteger => Convert.ToString(value, CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("O", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("O", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        IDictionary dictionary => WireDictionary(dictionary, BeforeArm),
        IEnumerable sequence and not string => sequence.Cast<object?>().Select(BeforeArm).ToArray(),
        _ => value,
    };

    // ---- after: in-range integers stay JSON numbers (reuse the incoming box) ----
    private static object? AfterArm(object? value) => value switch
    {
        null => null,
        long l => IsJsSafe(l) ? value : l.ToString(CultureInfo.InvariantCulture),
        ulong ul => ul <= (ulong)MaxSafeInteger ? value : ul.ToString(CultureInfo.InvariantCulture),
        decimal or BigInteger => Convert.ToString(value, CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("O", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("O", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        IDictionary dictionary => WireDictionary(dictionary, AfterArm),
        IEnumerable sequence and not string => sequence.Cast<object?>().Select(AfterArm).ToArray(),
        _ => value,
    };

    private static bool IsJsSafe(long value) => value is >= -MaxSafeInteger and <= MaxSafeInteger;

    private static Dictionary<string, object?> WireDictionary(IDictionary dictionary, Func<object?, object?> arm)
    {
        var projected = new Dictionary<string, object?>(dictionary.Count, StringComparer.Ordinal);
        foreach (DictionaryEntry entry in dictionary)
        {
            projected[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty] = arm(entry.Value);
        }

        return projected;
    }
}
