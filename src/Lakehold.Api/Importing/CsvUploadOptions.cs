namespace Lakehold.Api.Importing;

/// <summary>
///     Limits shared by browser CSV and XLSX imports. The existing configuration section name is
///     retained for deployment compatibility.
/// </summary>
public sealed class CsvUploadOptions
{
    public const string SectionName = "Lakehold:CsvImport";
    public const long DefaultMaxBytes = 5L * 1024 * 1024 * 1024;
    public const long DefaultMinimumFreeBytes = 1024L * 1024 * 1024;

    /// <summary>Maximum streamed file size accepted by one import request.</summary>
    public long MaxBytes { get; set; } = DefaultMaxBytes;

    /// <summary>Maximum bytes reserved by all in-flight uploads on this node.</summary>
    public long MaxAggregateScratchBytes { get; set; } = DefaultMaxBytes;

    /// <summary>Maximum uploads that may write node-local scratch concurrently.</summary>
    public int MaxConcurrentUploads { get; set; } = 2;

    /// <summary>Free filesystem capacity LakeHold will preserve after accepting a reservation.</summary>
    public long MinimumFreeBytes { get; set; } = DefaultMinimumFreeBytes;

    /// <summary>
    ///     Optional dedicated scratch directory. Empty uses a LakeHold-owned directory below the
    ///     platform temporary root.
    /// </summary>
    public string ScratchRoot { get; set; } = string.Empty;

    /// <summary>Age after which abandoned scratch files are removed during coordinator startup.</summary>
    public TimeSpan StaleFileAge { get; set; } = TimeSpan.FromHours(24);
}
