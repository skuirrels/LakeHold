using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api.Endpoints;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>Administering who may reach a workspace.</summary>
public sealed class MemberEndpointsTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lakehold-member-api", Guid.NewGuid().ToString("N"));

    private ControlPlaneContext _context = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _context = new ControlPlaneContext(
            new DbContextOptionsBuilder<ControlPlaneContext>()
                .UseDuckDB($"Data Source={Path.Combine(_root, "cp.duckdb")}")
                .Options);
        await _context.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Slug = "demo", DisplayName = "Demo", CreatedUtc = DateTimeOffset.UtcNow };
        _context.Tenants.Add(tenant);
        await _context.SaveChangesAsync();

        _context.TenantMembers.AddRange(
            Member(tenant.Id, "ada", "Ada", TokenRole.Owner, MemberStatus.Active),
            Member(tenant.Id, "newcomer", "New Comer", TokenRole.Reader, MemberStatus.Pending));
        await _context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static TenantMember Member(
        int tenantId, string subject, string name, TokenRole role, MemberStatus status)
        => new()
        {
            TenantId = tenantId,
            Issuer = "https://idp.test",
            Subject = subject,
            DisplayName = name,
            Role = role,
            Status = status,
            CreatedUtc = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task Listing_puts_people_awaiting_approval_first()
    {
        var result = await MemberEndpoints.ListAsync("demo", _context, CancellationToken.None);
        var members = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<IReadOnlyList<TenantMemberDto>>>(
            result.Result).Value!;

        // The only row that needs an administrator to act. Sorting it under established members is
        // how a request to join goes unanswered for a week.
        Assert.Equal("pending", members[0].Status);
        Assert.Equal("newcomer", members[0].Subject);
        Assert.Equal(2, members.Count);
    }

    [Fact]
    public async Task Approving_a_newcomer_grants_the_role_it_is_given()
    {
        var pending = await _context.TenantMembers.FirstAsync(m => m.Subject == "newcomer");

        await MemberEndpoints.UpdateAsync(
            "demo", pending.Id, new UpdateTenantMemberRequest("editor", "active"), _context, CancellationToken.None);

        var updated = await _context.TenantMembers.AsNoTracking().FirstAsync(m => m.Subject == "newcomer");
        Assert.Equal(MemberStatus.Active, updated.Status);
        Assert.Equal(TokenRole.Editor, updated.Role);
    }

    [Fact]
    public async Task An_unrecognised_role_is_refused_rather_than_quietly_downgraded()
    {
        var member = await _context.TenantMembers.FirstAsync(m => m.Subject == "ada");

        var result = await MemberEndpoints.UpdateAsync(
            "demo", member.Id, new UpdateTenantMemberRequest("administrator", null), _context, CancellationToken.None);

        // Falling back to reader would look like the request worked, and be discovered later by
        // someone who cannot do their job.
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.BadRequest<string>>(result.Result);
        Assert.Equal(TokenRole.Owner, (await _context.TenantMembers.AsNoTracking()
            .FirstAsync(m => m.Subject == "ada")).Role);
    }

    [Fact]
    public async Task A_member_of_another_tenant_is_not_reachable_through_this_one()
    {
        var other = new Tenant { Slug = "other", DisplayName = "Other", CreatedUtc = DateTimeOffset.UtcNow };
        _context.Tenants.Add(other);
        await _context.SaveChangesAsync();
        var outsider = Member(other.Id, "outsider", "Out Sider", TokenRole.Owner, MemberStatus.Active);
        _context.TenantMembers.Add(outsider);
        await _context.SaveChangesAsync();

        // The route names a tenant and an id; the id alone must not be enough to reach across.
        var result = await MemberEndpoints.UpdateAsync(
            "demo", outsider.Id, new UpdateTenantMemberRequest(null, "suspended"), _context, CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NotFound<string>>(result.Result);
        Assert.Equal(MemberStatus.Active, (await _context.TenantMembers.AsNoTracking()
            .FirstAsync(m => m.Subject == "outsider")).Status);
    }

    [Fact]
    public async Task Removing_a_member_leaves_them_able_to_arrive_again()
    {
        var member = await _context.TenantMembers.FirstAsync(m => m.Subject == "newcomer");

        await MemberEndpoints.RemoveAsync("demo", member.Id, _context, CancellationToken.None);

        Assert.False(await _context.TenantMembers.AnyAsync(m => m.Subject == "newcomer"));
    }
}
