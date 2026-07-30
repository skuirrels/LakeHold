using Lakehold.ControlPlane.Data;
using Lakehold.Engine.Catalog;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.Importing;

/// <summary>Raised when a browser upload exceeds the configured tabular-import ceiling.</summary>
public sealed class TabularUploadTooLargeException(long maxBytes)
    : Exception($"The file exceeds the configured upload limit of {maxBytes} bytes.");

/// <summary>
///     Streams a browser upload into disposable node-local scratch space and runs the import before
///     removing it.
/// </summary>
public sealed class TabularUploadService(
    LakehouseService lakehouse,
    IOptions<CsvUploadOptions> options,
    TabularScratchSpace scratch)
{
    private readonly LakehouseService _lakehouse = lakehouse;
    private readonly CsvUploadOptions _options = options.Value;
    private readonly TabularScratchSpace _scratch = scratch;

    /// <summary>Imports one uploaded file; the scratch copy never survives the request.</summary>
    public async Task<TabularImportResult> ImportAsync(
        string tenant,
        string catalog,
        Stream content,
        long? contentLength,
        string fileName,
        TabularFileFormat format,
        string schema,
        string table,
        bool automaticMode,
        CsvReadOptions readOptions,
        string? worksheet,
        int? tokenId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (contentLength > _options.MaxBytes)
        {
            throw new TabularUploadTooLargeException(_options.MaxBytes);
        }

        await using var lease = await _scratch
            .AcquireAsync(contentLength, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await using (var target = lease.OpenWrite())
            {
                var buffer = new byte[128 * 1024];
                long written = 0;
                while (true)
                {
                    var read = await content
                        .ReadAsync(buffer.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    written += read;
                    if (written > _options.MaxBytes)
                    {
                        throw new TabularUploadTooLargeException(_options.MaxBytes);
                    }

                    lease.EnsureReserved(written);
                    await target
                        .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    lease.RecordWritten(read);
                }

                if (written == 0)
                {
                    throw new ArgumentException("Choose a non-empty CSV or XLSX file.");
                }
            }

            return await _lakehouse
                .ImportTabularAsync(
                    tenant,
                    catalog,
                    lease.Path,
                    Path.GetFileName(fileName.Replace('\\', '/')),
                    format,
                    schema,
                    table,
                    automaticMode,
                    readOptions,
                    worksheet,
                    cancellationToken,
                    tokenId)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(lease.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Startup scavenging removes a file whose handle the platform retained or whose
                // directory temporarily became unavailable.
            }
        }
    }
}
