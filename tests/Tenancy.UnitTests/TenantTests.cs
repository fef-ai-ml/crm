using Crm.Kernel;
using Crm.Tenancy.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.Tenancy.UnitTests;

public class TenantTests
{
    private static readonly DateTime T0 = new(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_trims_the_name_and_starts_active()
    {
        var result = Tenant.Create("  Demo Inc  ", "demo", T0);

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("Demo Inc");
        result.Value.Slug.Should().Be("demo");
        result.Value.Status.Should().Be(TenantStatus.Active);
        result.Value.CreatedAt.Should().Be(T0);
        result.Value.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Suspend_and_activate_toggle_status()
    {
        var tenant = Tenant.Create("Demo Inc", "demo", T0).Value;

        tenant.Suspend();
        tenant.Status.Should().Be(TenantStatus.Suspended);

        tenant.Activate();
        tenant.Status.Should().Be(TenantStatus.Active);
    }

    [Theory]
    [InlineData("", "demo", "name")]
    [InlineData("Demo", "ab", "slug")]
    [InlineData("Demo", "Demo", "slug")]
    [InlineData("Demo", "has space", "slug")]
    public void Create_rejects_invalid_input_with_field_errors(string name, string slug, string field)
    {
        var result = Tenant.Create(name, slug, T0);

        result.IsFailure.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKind.Validation);
        result.Error.Fields.Should().ContainKey(field);
    }
}
