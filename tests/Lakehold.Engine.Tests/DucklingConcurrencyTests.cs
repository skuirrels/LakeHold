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

    /// <summary>
    ///     Disposal waits for a statement already inside the session instead of faulting it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Before this held, evicting a session disposed its <c>DbContext</c> and its gate out
    ///         from under whatever was running on it, and the in-flight caller died with
    ///         <c>ObjectDisposedException: … 'System.Threading.SemaphoreSlim'</c> in place of its
    ///         result. That was survivable while eviction only happened when an administrator changed
    ///         a catalog's configuration; it stopped being survivable when a committing statement
    ///         began evicting the catalog's reader, which puts eviction on the ordinary read/write
    ///         path.
    ///     </para>
    ///     <para>
    ///         Asserted from both sides: the operation completes with its own value, and the disposal
    ///         does not finish until it has.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Disposal_waits_for_a_statement_already_holding_the_gate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var inFlight = _duckling.InvokeAsync(
            async cancellationToken =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return "survived";
            },
            CancellationToken.None);
        await entered.Task;

        var disposal = _duckling.DisposeAsync().AsTask();

        // The session is busy, so disposal must still be pending. A generous wait, because proving a
        // negative quickly is how this assertion would pass on a machine that was merely slow.
        var finishedEarly = await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromMilliseconds(500)));
        Assert.NotSame(disposal, finishedEarly);

        release.SetResult();

        Assert.Equal("survived", await inFlight);
        await disposal.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
