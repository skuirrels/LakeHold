using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lakehold.Engine.Tests;

/// <summary>
///     Cover for the deliberately single-writer catalog session. Two callers may arrive together,
///     but only one operation may enter the EF Core/DuckDB session at a time.
/// </summary>
public sealed class DucklingConcurrencyTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "lakehold-concurrency",
        Guid.NewGuid().ToString("N"));
    private Duckling _duckling = null!;

    public async Task InitializeAsync()
    {
        var dataPath = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataPath);

        _duckling = await Duckling.StartAsync(
            new CatalogDescriptor(
                "concurrencylake",
                CatalogMetadataKind.LocalFile,
                Path.Combine(_root, "catalog.ducklake"),
                dataPath),
            new LakehouseOptions(),
            configure: null,
            NullLogger.Instance,
            CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _duckling.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failing must not fail the run.
        }
    }

    [Fact]
    public async Task A_second_operation_waits_for_the_catalog_gate()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = _duckling.InvokeAsync(
            async cancellationToken =>
            {
                firstEntered.SetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return "first";
            },
            CancellationToken.None);
        await firstEntered.Task;

        using var waitingCaller = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var second = _duckling.InvokeAsync(_ => Task.FromResult("second"), waitingCaller.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        releaseFirst.SetResult();
        Assert.Equal("first", await first);

        // Cancellation while queued must not consume or poison the permit.
        Assert.Equal(
            "third",
            await _duckling.InvokeAsync(_ => Task.FromResult("third"), CancellationToken.None));
    }
}
