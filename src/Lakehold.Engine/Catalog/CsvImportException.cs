using System.Globalization;
using System.Text.RegularExpressions;

namespace Lakehold.Engine.Catalog;

/// <summary>
///     A safe CSV import failure that never carries uploaded row contents or node-local paths.
/// </summary>
public sealed partial class CsvImportException : Exception
{
    public const string ImportErrorCode = "csv_import_error";
    public const string ParserErrorCode = "csv_parse_error";

    private CsvImportException(string message, bool isParserError)
        : base(message)
    {
        IsParserError = isParserError;
    }

    /// <summary>Whether automatic tolerant recovery is meaningful for this failure.</summary>
    public bool IsParserError { get; }

    /// <summary>Stable API error code for this failure category.</summary>
    public string Code => IsParserError ? ParserErrorCode : ImportErrorCode;

    internal static CsvImportException FromDuckDb(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var isParserError =
            message.Contains("CSV Error", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Error when sniffing file", StringComparison.OrdinalIgnoreCase);
        if (!isParserError)
        {
            return new CsvImportException(
                "DuckDB could not import the CSV with the selected settings.",
                isParserError: false);
        }

        var line = Value(LinePattern().Match(message), "line");
        var columns = ColumnCountPattern().Match(message);
        var expected = Value(columns, "expected");
        var found = Value(columns, "found");

        var detail = (line, expected, found) switch
        {
            ({ } row, { } expectedColumns, { } foundColumns) =>
                ColumnCountDetail(row, expectedColumns, foundColumns),
            ({ } row, _, _) =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"DuckDB could not parse CSV line {row} with the selected settings."),
            _ => "DuckDB could not parse the CSV with the selected settings.",
        };

        return new CsvImportException(detail, isParserError: true);
    }

    private static string ColumnCountDetail(long row, long expected, long found)
    {
        var columnLabel = found == 1 ? "column" : "columns";
        var expectedVerb = expected == 1 ? "was" : "were";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"CSV line {row} contains {found} {columnLabel}; {expected} {expectedVerb} expected.");
    }

    private static long? Value(Match match, string group)
        => match.Success
            && long.TryParse(
                match.Groups[group].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value)
                ? value
                : null;

    [GeneratedRegex(
        @"CSV Error on Line:\s*(?<line>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinePattern();

    [GeneratedRegex(
        @"Expected Number of Columns:\s*(?<expected>\d+)\s*Found:\s*(?<found>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ColumnCountPattern();
}
