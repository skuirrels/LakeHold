using Lakehold.Api.Auth;
using Lakehold.Api.Endpoints;
using Lakehold.Api.Scheduling;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     The scheduled-run read-out names every tenant and catalog the scheduler touched, so it is
///     projected to the caller exactly as the tenant listing is: an instance credential sees the whole
///     instance, a tenant credential sees only itself, and a catalog-narrowed credential sees only
///     that catalog. Reaching the route is the filter's decision; what comes back is this one's.
/// </summary>
public sealed class ScheduledRunVisibilityTests
{
    private static ScheduledRunLog Log()
    {
        var log = new ScheduledRunLog();
        var startedUtc = DateTimeOffset.UtcNow;

        log.Record(new ScheduledRun("flush", "demo", "analytics", startedUtc, 12, true, "flushed"));
        log.Record(new ScheduledRun("backup", "demo", "events", startedUtc, 34, true, "generation 3"));
        log.Record(new ScheduledRun("flush", "other", "otherlake", startedUtc, 56, false, "boom"));

        return log;
    }

    private static IReadOnlyList<ScheduledRunDto> Runs(ILakeholdPrincipal? principal)
    {
        var http = new DefaultHttpContext();
        if (principal is not null)
        {
            http.Items[LakeholdAuthorizationFilter.PrincipalItemKey] = principal;
        }

        return LakehouseEndpoints.GetScheduledRuns(http, Log()).Value!;
    }

    private static LakeholdPrincipal Tenant(string slug, string? catalog = null) => new(
        IsAuthenticated: true,
        Scope: TokenScope.Tenant,
        TenantId: 1,
        TenantSlug: slug,
        CatalogName: catalog,
        IsReadOnly: false,
        TokenId: 1);

    [Fact]
    public void A_tenant_credential_sees_only_its_own_runs()
    {
        var runs = Runs(Tenant("demo"));

        Assert.Equal(2, runs.Count);
        Assert.All(runs, r => Assert.Equal("demo", r.Tenant));
    }

    [Fact]
    public void A_catalog_narrowed_credential_sees_only_that_catalogs_runs()
    {
        var runs = Runs(Tenant("demo", catalog: "analytics"));

        var run = Assert.Single(runs);
        Assert.Equal("demo", run.Tenant);
        Assert.Equal("analytics", run.Catalog);
    }

    [Fact]
    public void An_instance_credential_sees_every_tenants_runs()
    {
        var runs = Runs(new LakeholdPrincipal(
            IsAuthenticated: true,
            Scope: TokenScope.Instance,
            TenantId: null,
            TenantSlug: null,
            CatalogName: null,
            IsReadOnly: false,
            TokenId: 2));

        Assert.Equal(3, runs.Count);
        Assert.Contains(runs, r => r.Tenant == "other");
    }

    [Fact]
    public void A_token_less_caller_still_sees_everything()
    {
        // The transitional open path: while Lakehold:Auth:RequireAuthentication is false a token-less
        // request trusts the route, and this read-out must not become the one surface that breaks it.
        Assert.Equal(3, Runs(principal: null).Count);
        Assert.Equal(3, Runs(LakeholdPrincipal.Legacy).Count);
    }
}
