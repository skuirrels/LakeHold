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
///     <para>
///         A scheduled backup and flush starting on the same second shared one warm session. The
///         flush's sweep judged it idle while the backup had just taken it, disposed it, and the
///         backup failed with <see cref="ObjectDisposedException"/>. Checking a session out now
///         counts as use, and the sweep re-checks that under the same lock the checkout takes.
///     </para>
///     <para>
///         The pool reads a fake clock that the test moves by hours, against a one-hour timeout. A
///         statement still stamps the session from the real clock, but a test run is seconds long,
///         so that stamp is effectively the clock's start. An earlier wall-clock version needed its
///         steps to land within a fraction of a three-second timeout and failed on a loaded runner.
///     </para>
/// </remarks>
public sealed class DucklingPoolCheckoutTests : IAsyncLifetime
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(1);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
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
            NullLoggerFactory.Instance,
            configurators: null,
            timeProvider: _clock);

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

        var first = await _pool.GetOrStartAsync(held, configure: null, CancellationToken.None);
        await first.ExecuteQueryAsync("SELECT 1", CancellationToken.None);

        // Not yet idle, so this checkout's own sweep keeps the session. The caller holds it and has
        // executed nothing on it yet: where the backup was when it failed.
        _clock.Advance(IdleTimeout * 0.8);
        var checkedOut = await _pool.GetOrStartAsync(held, configure: null, CancellationToken.None);
        Assert.Same(first, checkedOut);

        // Another catalog's request sweeps. From its last statement the held session is idle beyond
        // the timeout; from its checkout it is not.
        _clock.Advance(IdleTimeout * 0.5);
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

        var session = await _pool.GetOrStartAsync(idle, configure: null, CancellationToken.None);
        await session.ExecuteQueryAsync("SELECT 1", CancellationToken.None);

        _clock.Advance(IdleTimeout * 1.3);
        await _pool.GetOrStartAsync(other, configure: null, CancellationToken.None);

        Assert.DoesNotContain("idle", _pool.WarmCatalogs);
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

    /// <summary>A clock that moves only when told to.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
