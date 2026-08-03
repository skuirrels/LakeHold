using Lakehold.Api.PublicApi;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Lakehold.Api.Tests;

public sealed class SnapshotCursorTests
{
    private readonly IDataProtectionProvider _provider = new EphemeralDataProtectionProvider();

    [Fact]
    public void RoundTripsTheFrozenNativeKeyset()
    {
        var scope = SnapshotCursor.Scope("tenant", "catalog", null, null);
        var cursor = SnapshotCursor.Encode(_provider, scope, 42, 37);

        var valid = SnapshotCursor.TryDecode(_provider, cursor, scope, out var position);

        Assert.True(valid);
        Assert.Equal(42, position.UpperSnapshotInclusive);
        Assert.Equal(37, position.BeforeSnapshotExclusive);
    }

    [Fact]
    public void RefusesACursorFromAnotherRequestScope()
    {
        var cursor = SnapshotCursor.Encode(
            _provider,
            SnapshotCursor.Scope("tenant", "catalog", null, null),
            42,
            37);

        var valid = SnapshotCursor.TryDecode(
            _provider,
            cursor,
            SnapshotCursor.Scope("tenant", "other-catalog", null, null),
            out _);

        Assert.False(valid);
    }

    [Fact]
    public void RefusesTamperingWithoutLeakingAProtectionFailure()
    {
        var scope = SnapshotCursor.Scope("tenant", "catalog", null, null);
        var cursor = SnapshotCursor.Encode(_provider, scope, 42, 37);
        var replacement = cursor[^1] == 'A' ? 'B' : 'A';

        Assert.False(SnapshotCursor.TryDecode(
            _provider,
            cursor[..^1] + replacement,
            scope,
            out _));
    }
}
