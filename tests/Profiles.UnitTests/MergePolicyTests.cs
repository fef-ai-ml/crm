using Crm.Profiles.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class MergePolicyTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTime TenOClock = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UT010_merge_winner_is_the_older_profile()
    {
        var p1 = Profile.Create(Tenant, TenOClock);
        var p2 = Profile.Create(Tenant, TenOClock.AddHours(1));

        MergePolicy.Decide(p1, p2).Should().Be((p1, p2));
        MergePolicy.Decide(p2, p1).Should().Be((p1, p2));
    }

    [Fact]
    public void UT010_equal_first_seen_falls_back_to_the_smaller_id_so_the_choice_is_stable()
    {
        var a = Profile.Create(Tenant, TenOClock);
        var b = Profile.Create(Tenant, TenOClock);
        var expectedWinner = a.Id.CompareTo(b.Id) < 0 ? a : b;

        MergePolicy.Decide(a, b).Winner.Should().BeSameAs(expectedWinner);
        MergePolicy.Decide(b, a).Winner.Should().BeSameAs(expectedWinner);
    }
}
