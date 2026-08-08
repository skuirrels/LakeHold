using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Xunit;

namespace Lakehold.Api.Tests;

public sealed class QueryAuditContextTests
{
    [Fact]
    public void A_member_and_token_cannot_be_recorded_as_the_same_actor()
    {
        var principal = new LakeholdPrincipal(
            TokenScope.Tenant,
            TenantId: 1,
            TenantSlug: "demo",
            CatalogName: null,
            IsReadOnly: false,
            TokenId: 2,
            MemberId: 3);

        var exception = Assert.Throws<InvalidOperationException>(
            () => QueryAuditContext.From(principal, QueryOrigin.Mcp));

        Assert.Contains("both", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_system_administrator_without_a_token_is_a_system_actor()
    {
        var principal = new LakeholdPrincipal(
            TokenScope.Instance,
            TenantId: null,
            TenantSlug: null,
            CatalogName: null,
            IsReadOnly: false,
            TokenId: null);

        var audit = QueryAuditContext.From(principal, QueryOrigin.Rest);

        Assert.Equal(QueryActorKind.System, audit.ActorKind);
        Assert.Null(audit.TokenId);
        Assert.Null(audit.MemberId);
    }

    [Fact]
    public void A_richer_context_cannot_disagree_with_the_legacy_token_argument()
    {
        var audit = QueryAuditContext.FromToken(7, QueryOrigin.PgWire);

        var exception = Assert.Throws<InvalidOperationException>(
            () => QueryAuditContext.Resolve(8, audit));

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
    }
}
