namespace Lakehold.Engine.Catalog;

/// <summary>
///     A safe XLSX import failure whose message contains neither workbook cells nor scratch paths.
/// </summary>
public sealed class XlsxImportException : Exception
{
    public const string ImportErrorCode = "xlsx_import_error";
    public const string ExtensionUnavailableCode = "xlsx_extension_unavailable";

    private XlsxImportException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Stable machine-readable reason returned by the API.</summary>
    public string Code { get; }

    /// <summary>Translates an untrusted DuckDB diagnostic into a bounded safe failure.</summary>
    public static XlsxImportException FromDuckDb(string? diagnostic)
    {
        if (diagnostic?.Contains(
                "Failed to download extension \"excel\"",
                StringComparison.OrdinalIgnoreCase) is true)
        {
            return new XlsxImportException(
                ExtensionUnavailableCode,
                "XLSX support is unavailable because this node could not load DuckDB's Excel extension.");
        }

        return new XlsxImportException(
            ImportErrorCode,
            "DuckDB could not import the XLSX workbook. Confirm that it is a valid .xlsx file "
            + "and that the selected worksheet contains a rectangular data region.");
    }
}
