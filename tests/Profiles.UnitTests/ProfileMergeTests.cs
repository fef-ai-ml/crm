using System.Text.Json;
using Crm.Profiles.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class ProfileMergeTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTime T0 = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UT012_merge_repoints_identities_and_marks_the_loser()
    {
        var winner = Profile.Create(Tenant, T0);
        var loser = Profile.Create(Tenant, T0.AddHours(1));
        var identities = new[]
        {
            new UserIdentity(Tenant, IdentityKey.AnonymousId("a1"), loser.Id),
            new UserIdentity(Tenant, IdentityKey.Email("x@y.z")!.Value, loser.Id),
        };

        var merged = ProfileMerge.Apply(winner, loser, identities);

        identities.Should().OnlyContain(i => i.ProfileId == winner.Id);
        loser.MergedIntoId.Should().Be(winner.Id);
        merged.WinnerId.Should().Be(winner.Id);
        merged.LoserId.Should().Be(loser.Id);
    }

    [Fact]
    public void UT012_the_winner_adopts_contact_points_it_lacks_and_the_loser_gives_them_up()
    {
        var winner = Profile.Create(Tenant, T0);
        var loser = Profile.Create(Tenant, T0.AddHours(1));
        loser.AssignExternalId("u1");
        loser.AssignEmail("x@y.z");
        winner.AssignPhone("+628123456");
        loser.AssignPhone("+14155550101");

        ProfileMerge.Apply(winner, loser, []);

        winner.ExternalId.Should().Be("u1");
        winner.Email.Should().Be("x@y.z");
        winner.Phone.Should().Be("+628123456", "the winner's own value wins");
        loser.ExternalId.Should().BeNull("the unique index allows one owner per value");
        loser.Email.Should().BeNull();
        loser.Phone.Should().BeNull();
    }

    [Fact]
    public void UT012_attributes_merge_and_last_seen_is_the_later_of_the_two()
    {
        var winner = Profile.Create(Tenant, T0);
        var loser = Profile.Create(Tenant, T0.AddHours(1));
        using var winnerTraits = JsonDocument.Parse("""{"plan":"pro"}""");
        using var loserTraits = JsonDocument.Parse("""{"plan":"free","country":"ID"}""");
        winner.ApplyIdentify(winnerTraits.RootElement, T0);
        loser.ApplyIdentify(loserTraits.RootElement, T0.AddHours(2));

        var merged = ProfileMerge.Apply(winner, loser, []);

        merged.GainedKeys.Should().Equal("country");
        winner.Attributes.Should().Contain("\"plan\":\"pro\"").And.Contain("\"country\":\"ID\"");
        winner.LastSeenAt.Should().Be(T0.AddHours(2));
        winner.FirstSeenAt.Should().Be(T0);
    }

    [Fact]
    public void A_profile_cannot_be_merged_into_itself_or_twice()
    {
        var winner = Profile.Create(Tenant, T0);
        var loser = Profile.Create(Tenant, T0.AddHours(1));
        ProfileMerge.Apply(winner, loser, []);

        var again = () => ProfileMerge.Apply(winner, loser, []);
        var self = () => ProfileMerge.Apply(winner, winner, []);

        again.Should().Throw<InvalidOperationException>();
        self.Should().Throw<InvalidOperationException>();
    }
}
