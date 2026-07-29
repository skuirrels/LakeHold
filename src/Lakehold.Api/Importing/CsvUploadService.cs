using Lakehold.ControlPlane.Data;
using Lakehold.Engine.Catalog;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.Importing;

/// <summary>Raised when a browser upload exceeds the configured CSV import ceiling.</summary>
public sealed class CsvUploadTooLargeException(long maxBytes)
    : Exception($"The CSV exceeds the configured upload limit of {maxBytes} bytes.");

/// <summary>
///     Streams a browser upload into disposable node-local scratch space and runs the import before
///     removing it.
/// </summary>
public sealed class CsvUploadService(
    LakehouseService lakehouse,
    IOptions<CsvUploadOptions> options,
    CsvScratchSpace scratch)
{
    private readonly LakehouseService _lakehouse = lakehouse;
    private readonly CsvUploadOptions _options = options.Value;
    private readonly CsvScratchSpace _scratch = scratch;

    /// <summary>Imports one uploaded file; the scratch copy never survives the request.</summary>
    public async Task<CsvImportResult> ImportAsync(
        string tenant,
        string catalog,
        Stream content,
        long? contentLength,
        string fileName,
        string schema,
        string table,
        CsvReadOptions readOptions,
        int? tokenId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (contentLength > _options.MaxBytes)
        {
            throw new CsvUploadTooLargeException(_options.MaxBytes);
        }

        await using var lease = await _scratch
            .AcquireAsync(contentLength, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await using (var target = new FileStream(
                             lease.Path,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
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
                        throw new CsvUploadTooLargeException(_options.MaxBytes);
                    }

                    lease.EnsureReserved(written);
                    await target
                        .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    lease.RecordWritten(read);
                }

                if (written == 0)
                {
                    throw new ArgumentException("Choose a non-empty CSV file.");
                }
            }

            return await _lakehouse
                .ImportCsvAsync(
                    tenant,
                    catalog,
                    lease.Path,
                    Path.GetFileName(fileName.Replace('\\', '/')),
                    schema,
                    table,
                    readOptions,
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
