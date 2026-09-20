using System.Text.Json;
using Crm.Profiles.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class ApplyEventTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTime T0 = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UT014_track_and_identify_update_different_counters()
    {
        var profile = Profile.Create(Tenant, T0);
        profile.SetEventCount(5);
        using var traits = JsonDocument.Parse("""{"plan":"pro"}""");

        profile.ApplyTrack(T0.AddMinutes(1));
        profile.EventCount.Should().Be(6);
        profile.LastSeenAt.Should().Be(T0.AddMinutes(1));

        profile.ApplyIdentify(traits.RootElement, T0.AddMinutes(2));
        profile.EventCount.Should().Be(6);
        profile.LastSeenAt.Should().Be(T0.AddMinutes(2));
    }

    [Fact]
    public void UT014_last_seen_never_moves_backwards()
    {
        var profile = Profile.Create(Tenant, T0);

        profile.ApplyTrack(T0.AddMinutes(-5));

        profile.LastSeenAt.Should().Be(T0);
        profile.FirstSeenAt.Should().Be(T0);
        profile.EventCount.Should().Be(1);
    }

    [Fact]
    public void A_new_profile_is_empty_and_belongs_to_the_tenant()
    {
        var profile = Profile.Create(Tenant, T0);

        profile.TenantId.Should().Be(Tenant);
        profile.Attributes.Should().Be("{}");
        profile.EventCount.Should().Be(0);
        profile.MergedIntoId.Should().BeNull();
        profile.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void External_id_can_be_assigned_once()
    {
        var profile = Profile.Create(Tenant, T0);
        profile.AssignExternalId("u1");

        profile.AssignExternalId("u1");
        var act = () => profile.AssignExternalId("u2");

        profile.ExternalId.Should().Be("u1");
        act.Should().Throw<InvalidOperationException>();
    }
}
