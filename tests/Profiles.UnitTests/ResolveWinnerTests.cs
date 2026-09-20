using Crm.Profiles.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class ResolveWinnerTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTime T0 = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task UT013_applier_follows_MergedIntoId_to_the_winner()
    {
        var c = Profile.Create(Tenant, T0);
        var b = Profile.Create(Tenant, T0.AddHours(1));
        var a = Profile.Create(Tenant, T0.AddHours(2));
        ProfileMerge.Apply(b, a, []); // a -> b (both live at this point)
        ProfileMerge.Apply(c, b, []); // b -> c, so a -> b -> c
        var byId = new Dictionary<Guid, Profile> { [a.Id] = a, [b.Id] = b, [c.Id] = c };

        var winner = await WinnerResolver.ResolveAsync(a, (id, _) => Task.FromResult(byId.GetValueOrDefault(id)), CancellationToken.None);

        winner.Should().BeSameAs(c);
    }

    [Fact]
    public async Task UT013_a_profile_that_was_never_merged_is_its_own_winner()
    {
        var a = Profile.Create(Tenant, T0);

        var winner = await WinnerResolver.ResolveAsync(a, (_, _) => throw new InvalidOperationException("must not load"), CancellationToken.None);

        winner.Should().BeSameAs(a);
    }

    [Fact]
    public async Task UT013_a_cycle_throws_MergeChainCorruptException()
    {
        var a = Profile.Create(Tenant, T0);
        var b = Profile.Create(Tenant, T0.AddHours(1));
        ProfileMerge.Apply(a, b, []);          // b -> a
        ForceMergedInto(a, b.Id);              // a -> b, which the aggregate itself refuses to do
        var byId = new Dictionary<Guid, Profile> { [a.Id] = a, [b.Id] = b };

        var act = () => WinnerResolver.ResolveAsync(a, (id, _) => Task.FromResult(byId.GetValueOrDefault(id)), CancellationToken.None);

        await act.Should().ThrowAsync<MergeChainCorruptException>();
    }

    [Fact]
    public async Task UT013_a_dangling_pointer_throws_MergeChainCorruptException()
    {
        var winner = Profile.Create(Tenant, T0);
        var loser = Profile.Create(Tenant, T0.AddHours(1));
        ProfileMerge.Apply(winner, loser, []);

        var act = () => WinnerResolver.ResolveAsync(loser, (_, _) => Task.FromResult<Profile?>(null), CancellationToken.None);

        await act.Should().ThrowAsync<MergeChainCorruptException>();
    }

    /// <summary>Corrupt data can only come from outside the aggregate, so the test writes the property the way a bad row would.</summary>
    private static void ForceMergedInto(Profile profile, Guid target)
        => typeof(Profile).GetProperty(nameof(Profile.MergedIntoId))!.SetValue(profile, target);
}
