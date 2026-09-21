using Crm.Profiles.Application.Seeding;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests.Seeding;

public sealed class SeedDatasetGeneratorTests
{
    private static readonly SeedData Data = SeedDatasetGenerator.Generate();
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);
    private static readonly TimeSpan Month = TimeSpan.FromDays(30);

    [Fact]
    public void Two_runs_produce_the_same_dataset()
    {
        var again = SeedDatasetGenerator.Generate();
        again.Profiles.SequenceEqual(Data.Profiles).Should().BeTrue();
        again.Events.SequenceEqual(Data.Events).Should().BeTrue();
        again.Merges.SequenceEqual(Data.Merges).Should().BeTrue();
    }

    [Fact]
    public void Totals_match_the_fixture_document()
    {
        Data.Profiles.Should().HaveCount(5000);
        Data.Events.Should().HaveCount(100_000);
        Data.Merges.Should().HaveCount(50);
        Data.Profiles.Select(p => p.ExternalId).Should().OnlyHaveUniqueItems().And.Contain("u-00001").And.Contain("u-05000");
        Data.Profiles.Select(p => p.Email).Should().OnlyHaveUniqueItems();
        Data.Profiles.Single(p => p.Index == 1).Email.Should().Be("alice.1@example.test");
        Data.Profiles.Where(p => p.Phone is not null).Should().HaveCount(3000).And.OnlyContain(p => p.Phone!.StartsWith("+62812") && p.Phone.Length == 13);
    }

    [Fact]
    public void Attribute_marginals_are_exact()
    {
        Count(p => p.Plan == "pro").Should().Be(1000);
        Count(p => p.Plan == "free").Should().Be(3500);
        Count(p => p.Plan is null).Should().Be(500);
        Count(p => p.Country == "ID").Should().Be(2000);
        Count(p => p.Country == "SG").Should().Be(1250);
        Count(p => p.Country == "MY").Should().Be(750);
        Count(p => p.Country == "TH").Should().Be(500);
        Count(p => p.Country is null).Should().Be(500);
        Count(p => p.City is not null).Should().Be(2500);
        Count(p => p.OptedIn == true).Should().Be(1500);
        Count(p => p.OptedIn == false).Should().Be(1500);
        Count(p => p.Ltv is not null).Should().Be(4000);
    }

    [Fact]
    public void Event_name_mix_is_exact_and_amounts_belong_to_purchases()
    {
        Data.Events.Count(e => e.Name == "app_open").Should().Be(60_000);
        Data.Events.Count(e => e.Name == "purchase").Should().Be(20_000);
        Data.Events.Count(e => e.Name == "add_to_cart").Should().Be(15_000);
        Data.Events.Count(e => e.Name == "logout").Should().Be(5_000);
        Data.Events.Should().OnlyContain(e => (e.Name == "purchase") == (e.Amount != null));
        Data.Events.Where(e => e.Amount is not null).Should().OnlyContain(e => e.Amount >= 5m && e.Amount <= 200m);
        Data.Events.Should().OnlyContain(e => e.Age >= TimeSpan.FromHours(1) && e.Age < TimeSpan.FromDays(90));
    }

    [Fact]
    public void Activity_mix_is_exact()
    {
        var withEvents = Data.Events.Select(e => e.ProfileIndex).ToHashSet();
        Data.Profiles.Count(p => !withEvents.Contains(p.Index)).Should().Be(250, "5 % have no events at all");
        var recent = Data.Events.Where(e => e.Age < Week).Select(e => e.ProfileIndex).ToHashSet();
        Data.Profiles.Count(p => !recent.Contains(p.Index)).Should().Be(1500, "30 % have no event in the last 7 days");
    }

    [Fact]
    public void The_six_documented_segment_counts_hold_at_load_time_and_four_days_later()
    {
        foreach (var drift in new[] { TimeSpan.Zero, TimeSpan.FromDays(4) })
        {
            var appOpenIn7d = Data.Events.Where(e => e.Name == "app_open" && e.Age + drift < Week).Select(e => e.ProfileIndex).ToHashSet();
            var purchaseIn30d = Data.Events.Where(e => e.Name == "purchase" && e.Age + drift < Month).Select(e => e.ProfileIndex).ToHashSet();

            var actual = new Dictionary<string, int>
            {
                ["seed-pro-id"] = Count(p => p.Plan == "pro" && (p.Country is "ID" or "SG" || !appOpenIn7d.Contains(p.Index))),
                ["seed-purchasers"] = Count(p => purchaseIn30d.Contains(p.Index)),
                ["seed-dormant"] = Count(p => !appOpenIn7d.Contains(p.Index)),
                ["seed-no-plan"] = Count(p => p.Plan is null),
                ["seed-not-pro"] = Count(p => p.Plan != "pro"),
                ["seed-high-ltv-sg"] = Count(p => p.Ltv >= 150m && p.Country == "SG"),
            };

            actual.Should().Equal(SeedExpectations.Segments, $"drift {drift.TotalDays} d");
        }

        SeedExpectations.Segments.Should().Equal(new Dictionary<string, int>
        {
            ["seed-pro-id"] = 412, ["seed-purchasers"] = 2731, ["seed-dormant"] = 1500,
            ["seed-no-plan"] = 500, ["seed-not-pro"] = 4000, ["seed-high-ltv-sg"] = 187,
        });
    }

    [Fact]
    public void Pinned_profile_is_an_active_pro_purchaser_in_ID_with_more_than_120_events()
    {
        var alice = Data.Profiles.Single(p => p.ExternalId == SeedExpectations.PinnedExternalId);
        alice.Should().Match<SeedProfile>(p => p.Plan == "pro" && p.Country == "ID" && !p.Dormant && p.Purchaser);
        Data.Events.Count(e => e.ProfileIndex == alice.Index).Should().BeGreaterThan(120);
    }

    [Fact]
    public void Merges_point_at_distinct_existing_winners()
    {
        Data.Merges.Select(m => m.AnonymousId).Should().OnlyHaveUniqueItems();
        Data.Merges.Select(m => m.WinnerIndex).Should().OnlyHaveUniqueItems().And.OnlyContain(i => i >= 1 && i <= 5000);
    }

    private static int Count(Func<SeedProfile, bool> predicate) => Data.Profiles.Count(predicate);
}
