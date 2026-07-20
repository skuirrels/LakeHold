using System.Diagnostics.CodeAnalysis;

namespace Lakehold.Engine.Catalog;

/// <summary>
///     Path handling that works for both local filesystems and object stores.
/// </summary>
/// <remarks>
///     <para>
///         Lakehold's headline claim is bring-your-own-bucket, so a data path is as likely to be
///         <c>s3://bucket/analytics</c> as <c>/var/lib/lakehold/data</c>. <see cref="Path.Combine"/>
///         mangles the first — on Unix it collapses the double slash — and
///         <see cref="Directory.CreateDirectory"/> would then create a literal <c>s3:</c> folder on
///         local disk. The first version of catalog backup did exactly that and silently worked only
///         for local deployments.
///     </para>
///     <para>
///         DuckDB itself is agnostic: <c>COPY … TO 's3://…'</c> works given credentials. So the rule
///         is to keep composition textual and let DuckDB do the I/O, touching
///         <see cref="System.IO"/> only when the location is genuinely local.
///     </para>
/// </remarks>
public static class StorageLocation
{
    private static readonly string[] RemoteSchemes = ["s3://", "gs://", "gcs://", "az://", "azure://", "abfss://", "r2://", "http://", "https://"];

    /// <summary>Whether <paramref name="location"/> addresses an object store rather than local disk.</summary>
    public static bool IsRemote([NotNullWhen(true)] string? location)
        => location is not null
           && RemoteSchemes.Any(s => location.StartsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     Appends <paramref name="segments"/> to <paramref name="root"/>, preserving URI form.
    /// </summary>
    public static string Combine(string root, params string[] segments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(segments);

        if (!IsRemote(root))
        {
            return Path.Combine([root, .. segments]);
        }

        // Object-store keys are '/'-delimited regardless of host platform, so this must not go
        // through Path.Combine — on Windows that would emit backslashes into an S3 key.
        var trimmed = root.TrimEnd('/');
        return segments.Aggregate(trimmed, (acc, s) => $"{acc}/{s.Trim('/')}");
    }

    /// <summary>
    ///     Ensures <paramref name="location"/> exists as a directory, where that concept applies.
    /// </summary>
    /// <remarks>
    ///     Object stores have no directories — a prefix springs into existence when an object is
    ///     written under it — so this is a no-op for remote locations rather than an error.
    /// </remarks>
    public static void EnsureDirectory(string location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);

        if (!IsRemote(location))
        {
            Directory.CreateDirectory(location);
        }
    }

    /// <summary>
    ///     Total size in bytes of the files directly under <paramref name="location"/>, or null when
    ///     the location is remote and the size cannot be established without listing the store.
    /// </summary>
    public static long? TryMeasureBytes(string location)
    {
        if (IsRemote(location) || !Directory.Exists(location))
        {
            return null;
        }

        return new DirectoryInfo(location).EnumerateFiles().Sum(f => f.Length);
    }

    /// <summary>
    ///     Immediate child directory names under <paramref name="location"/>, newest name last.
    ///     Empty when the location is remote or missing.
    /// </summary>
    public static IReadOnlyList<string> ListChildDirectories(string location)
    {
        if (IsRemote(location) || !Directory.Exists(location))
        {
            return [];
        }

        return [.. new DirectoryInfo(location).EnumerateDirectories().Select(d => d.Name).Order(StringComparer.Ordinal)];
    }
}
