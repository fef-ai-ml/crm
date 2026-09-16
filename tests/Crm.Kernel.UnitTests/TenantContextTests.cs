#pragma warning disable IDE0005 // Using directive is unnecessary
using Crm.Kernel.Tenancy;
using FluentAssertions;
using Xunit;
#pragma warning restore IDE0005

namespace Crm.Kernel.UnitTests;

[Trait("Category", "Unit")]
public class TenantContextTests
{
    [Fact]
    public void Unresolved_context_throws_on_TenantId()
    {
        var ctx = new TenantContext();

        ctx.IsResolved.Should().BeFalse();
        ctx.Kind.Should().Be(TenantPrincipalKind.None);
        var act = () => ctx.TenantId;
        act.Should().Throw<TenantContextMissingException>();
    }

    [Fact]
    public void Resolve_sets_all_fields()
    {
        var ctx = new TenantContext();
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();

        ctx.Resolve(tenant, user, TenantPrincipalKind.User);

        ctx.IsResolved.Should().BeTrue();
        ctx.TenantId.Should().Be(tenant);
        ctx.UserId.Should().Be(user);
        ctx.Kind.Should().Be(TenantPrincipalKind.User);
    }

    [Fact]
    public void Resolve_twice_throws()
    {
        var ctx = new TenantContext();
        ctx.Resolve(Guid.NewGuid(), null, TenantPrincipalKind.ApiKey);

        var act = () => ctx.Resolve(Guid.NewGuid(), null, TenantPrincipalKind.ApiKey);

        act.Should().Throw<InvalidOperationException>();
    }
}
