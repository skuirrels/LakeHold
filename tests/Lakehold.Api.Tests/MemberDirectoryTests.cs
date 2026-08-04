using System.Security.Claims;
using DuckDB.EFCoreProvider.Extensions;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Membership, not claims, decides what a signed-in person reaches.
/// </summary>
/// <remarks>
///     These replace tests for a resolver that read the tenant straight off a claim. The difference
///     they exist to prove is that access is now a row an administrator owns: granting, demoting, and
///     revoking take effect here rather than requiring someone to edit the identity provider, and a
///     provider that keeps asserting a stale role cannot undo a decision made in LakeHold.
/// </remarks>
public sealed class MemberDirectoryTests : IAsyncLifetime
{
    private const string Issuer = "https://idp.test/realms/lakehold";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lakehold-members", Guid.NewGuid().ToString("N"));

    private ServiceProvider _services = null!;
    private int _tenantId;

    private static MemberClaimContract Contract() =>
        new(Issuer, "tenant", "role", "groups", "lakehold-administrators");

    private static ClaimsPrincipal User(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "oidc"));

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var services = new ServiceCollection();
        services.AddDbContext<ControlPlaneContext>(
            o => o.UseDuckDB($"Data Source={Path.Combine(_root, "cp.duckdb")}"));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<MemberDirectory>();
        _services = services.BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
        await context.Database.EnsureCreatedAsync();
        var tenant = new Tenant { Slug = "demo", DisplayName = "Demo", CreatedUtc = DateTimeOffset.UtcNow };
        context.Tenants.Add(tenant);
        await context.SaveChangesAsync();
        _tenantId = tenant.Id;
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A held file handle on a temporary directory is not worth failing a test over.
        }
    }

    private async Task<LakeholdPrincipal?> ResolveAsync(ClaimsPrincipal user)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MemberDirectory>()
            .ResolveAsync(user, Contract());
    }

    private async Task<TenantMember?> MemberAsync(string subject)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ControlPlaneContext>()
            .TenantMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Subject == subject);
    }

    private async Task SetAsync(string subject, Action<TenantMember> change)
    {
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
        var member = await context.TenantMembers.FirstAsync(m => m.Subject == subject);
        change(member);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task A_first_arrival_vouched_for_by_the_provider_becomes_a_member()
    {
        var principal = await ResolveAsync(
            User(("sub", "ada"), ("tenant", "demo"), ("role", "owner"), ("name", "Ada")));

        Assert.NotNull(principal);
        Assert.Equal("demo", principal.TenantSlug);
        Assert.Equal(TokenRole.Owner, principal.Role);

        // The point of the row: the person now exists in LakeHold and can be listed and changed.
        var member = await MemberAsync("ada");
        Assert.NotNull(member);
        Assert.Equal(MemberStatus.Active, member.Status);
        Assert.Equal(_tenantId, member.TenantId);
        Assert.Equal(Issuer, member.Issuer);
    }

    [Fact]
    public async Task A_demotion_in_Lakehold_survives_the_provider_reasserting_the_old_role()
    {
        var user = User(("sub", "ada"), ("tenant", "demo"), ("role", "owner"));
        Assert.Equal(TokenRole.Owner, (await ResolveAsync(user))!.Role);

        await SetAsync("ada", m => m.Role = TokenRole.Reader);

        // The claim still says owner. It is no longer the authority, which is the whole change: an
        // administrator's decision cannot be silently reverted by a provider nobody re-edited.
        var principal = await ResolveAsync(user);
        Assert.Equal(TokenRole.Reader, principal!.Role);
        Assert.True(principal.IsReadOnly);
    }

    [Fact]
    public async Task Revoking_a_member_takes_effect_on_their_next_request()
    {
        var user = User(("sub", "ada"), ("tenant", "demo"), ("role", "owner"));
        Assert.NotNull(await ResolveAsync(user));

        await SetAsync("ada", m => m.Status = MemberStatus.Suspended);

        Assert.Null(await ResolveAsync(user));
    }

    [Fact]
    public async Task A_pending_member_reaches_nothing()
    {
        var user = User(("sub", "ada"), ("tenant", "demo"));
        Assert.NotNull(await ResolveAsync(user));

        await SetAsync("ada", m => m.Status = MemberStatus.Pending);

        Assert.Null(await ResolveAsync(user));
    }

    [Fact]
    public async Task An_identity_naming_no_known_tenant_reaches_nothing_and_creates_nothing()
    {
        Assert.Null(await ResolveAsync(User(("sub", "stranger"), ("tenant", "does-not-exist"))));
        Assert.Null(await ResolveAsync(User(("sub", "stranger2"))));

        // Inventing a tenant, or attaching the person to an arbitrary one, would both be worse than
        // refusing. Nothing is recorded for an identity there is nowhere to put.
        Assert.Null(await MemberAsync("stranger"));
        Assert.Null(await MemberAsync("stranger2"));
    }

    [Fact]
    public async Task Membership_is_keyed_on_the_subject_rather_than_a_reusable_name()
    {
        await ResolveAsync(User(("sub", "ada"), ("tenant", "demo"), ("role", "owner"), ("email", "a@x.test")));

        // Same email, different person. A membership keyed on an address would hand the newcomer the
        // previous holder's access the moment an address is recycled.
        var other = await ResolveAsync(
            User(("sub", "someone-else"), ("tenant", "demo"), ("email", "a@x.test")));

        Assert.Equal(TokenRole.Reader, other!.Role);
        Assert.NotEqual(
            (await MemberAsync("ada"))!.Id,
            (await MemberAsync("someone-else"))!.Id);
    }

    [Fact]
    public async Task An_administrator_group_grants_the_instance_and_never_a_tenant()
    {
        var principal = await ResolveAsync(
            User(("sub", "root"), ("groups", "lakehold-administrators"), ("tenant", "demo")));

        Assert.NotNull(principal);
        Assert.Equal(TokenScope.Instance, principal.Scope);
        Assert.Null(principal.TenantSlug);

        // Instance administration provisions tenants, so it cannot be something a workspace
        // membership confers -- otherwise an owner could promote themselves.
        Assert.Null(await MemberAsync("root"));
    }

    [Fact]
    public async Task An_identity_with_no_subject_is_refused()
    {
        // Every later sign-in would look like a different person, so there is nothing to record.
        Assert.Null(await ResolveAsync(User(("tenant", "demo"), ("role", "owner"))));
    }
}
