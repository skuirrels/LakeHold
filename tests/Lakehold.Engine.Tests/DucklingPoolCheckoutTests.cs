using System.Diagnostics;
using System.Globalization;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Engine.Tests;

/// <summary>
///     Cover for the idle sweep reclaiming a session a caller has already been handed.
/// </summary>
/// <remarks>
///     A scheduled backup and flush starting on the same second shared one warm session. The flush's
///     sweep judged it idle while the backup had just taken it, disposed it, and the backup failed
///     with <see cref="ObjectDisposedException"/>. Checking a session out now counts as use, and the
///     sweep re-checks that under the same lock the checkout takes.
/// </remarks>
public sealed class DucklingPoolCheckoutTests : IAsyncLifetime
{
    // Large enough that the margins below survive a loaded CI runner, small enough to keep each test
    // to a few seconds. Below its five-second floor the sweep interval equals the idle timeout.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(3);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-tests", Guid.NewGuid().ToString("N"));
    private DucklingPool _pool = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _pool = new DucklingPool(
            Options.Create(new LakehouseOptions
            {
                DataRoot = Path.Combine(_root, "data"),
                IdleTimeout = IdleTimeout,
            }),
            NullLoggerFactory.Instance);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _pool.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failing must not fail the test run.
        }
    }

    [Fact]
    public async Task A_session_checked_out_but_not_yet_used_survives_the_next_sweep()
    {
        var held = Catalog("held", 1);
        var other = Catalog("other", 2);

        // Offsets are measured from this checkout, whose sweep opens the throttle window. `ready` is
        // when the session ran its last statement, after paying its own start-up.
        var clock = Stopwatch.StartNew();
        var first = await _pool.GetOrStartAsync(held, configure: null, CancellationToken.None);
        await first.ExecuteQueryAsync("SELECT 1", CancellationToken.None);
        var ready = clock.Elapsed;
        Assert.True(
            ready < IdleTimeout * 0.5,
            $"start-up took {ready}; the timeline below needs it under {IdleTimeout * 0.5}");

        // Inside the throttle window, so this checkout runs no sweep. The caller holds the session
        // and has executed nothing on it yet: where the backup was when it failed.
        await DelayUntilAsync(clock, IdleTimeout * 0.8);
        var checkedOut = await _pool.GetOrStartAsync(held, configure: null, CancellationToken.None);
        Assert.Same(first, checkedOut);

        // Past the window, so another catalog's request sweeps. From its last statement the held
        // session is idle beyond the timeout; from its checkout it is not.
        await DelayUntilAsync(clock, ready + (IdleTimeout * 1.2));
        await _pool.GetOrStartAsync(other, configure: null, CancellationToken.None);

        var result = await checkedOut.ExecuteQueryAsync("SELECT 42", CancellationToken.None);
        Assert.Equal(42, Convert.ToInt32(result.Rows[0][0], CultureInfo.InvariantCulture));
        Assert.Contains("held", _pool.WarmCatalogs);
    }

    [Fact]
    public async Task A_session_nobody_has_touched_is_still_evicted()
    {
        var idle = Catalog("idle", 1);
        var other = Catalog("other", 2);

        var clock = Stopwatch.StartNew();
        var session = await _pool.GetOrStartAsync(idle, configure: null, CancellationToken.None);
        await session.ExecuteQueryAsync("SELECT 1", CancellationToken.None);
        var ready = clock.Elapsed;

        await DelayUntilAsync(clock, ready + (IdleTimeout * 1.3));
        await _pool.GetOrStartAsync(other, configure: null, CancellationToken.None);

        Assert.DoesNotContain("idle", _pool.WarmCatalogs);
    }

    private static async Task DelayUntilAsync(Stopwatch clock, TimeSpan offset)
    {
        var remaining = offset - clock.Elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining);
        }
    }

    private CatalogDescriptor Catalog(string name, int id)
    {
        var descriptor = new CatalogDescriptor(
            name,
            CatalogMetadataKind.LocalFile,
            Path.Combine(_root, $"{name}.ducklake"),
            Path.Combine(_root, "data", name),
            TenantKey: "acme",
            CatalogId: id);
        Directory.CreateDirectory(descriptor.DataPath);
        return descriptor;
    }
}
