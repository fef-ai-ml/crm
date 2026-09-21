using Crm.Segmentation.Application.Seeding;
using Crm.Segmentation.Domain.Rules;
using Crm.Segmentation.Domain.Segments;
using FluentAssertions;
using Xunit;

namespace Crm.Segmentation.UnitTests;

/// <summary>Task-9 brief: the six constants `SeedSegments` ships for `POST /v1/dev/seed-segments` must mean
/// exactly what `tests/fixtures/rules/valid/seed-*.json` means (whitespace/key order may differ, the parsed tree
/// may not), and the catalog's classification (status, real-time eligibility) must match docs/test/fixtures.md.</summary>
public class SeedSegmentsTests
{
    public static TheoryData<string> SeedNames() => new(SeedSegments.All.Select(e => e.Name));

    [Fact]
    public void seed_segments_cover_exactly_the_six_seed_fixtures()
    {
        SeedSegments.All.Select(e => e.Name).Should().BeEquivalentTo(
            ["seed-pro-id", "seed-purchasers", "seed-dormant", "seed-no-plan", "seed-not-pro", "seed-high-ltv-sg"]);
    }

    [Theory]
    [MemberData(nameof(SeedNames))]
    public void each_seed_segment_hashes_equal_to_its_fixture_file(string name)
    {
        var entry = SeedSegments.All.Single(e => e.Name == name);
        var (fixtureDefinition, expectedErrorPath) = Fixtures.Load(name);
        expectedErrorPath.Should().BeNull("every seed-*.json fixture is valid");

        var seedTree = RuleParser.Parse(entry.Definition).Tree!;
        var fixtureTree = RuleParser.Parse(fixtureDefinition).Tree!;

        DefinitionHash.ToHex(DefinitionHash.Compute(seedTree)).Should().Be(DefinitionHash.ToHex(DefinitionHash.Compute(fixtureTree)),
            $"'{name}' must mean exactly what its fixture file means, independent of formatting");
    }

    [Fact]
    public void exactly_one_seed_segment_is_draft()
    {
        SeedSegments.All.Where(e => e.Status == SegmentStatus.Draft).Select(e => e.Name)
            .Should().Equal("seed-high-ltv-sg");
        SeedSegments.All.Where(e => e.Status == SegmentStatus.Active).Should().HaveCount(5);
    }

    [Fact]
    public void seed_purchasers_is_realtime_eligible_and_seed_pro_id_is_not()
    {
        Eligible("seed-purchasers").Should().BeTrue("a single gte-1 event leaf needs no knowledge of absent data");
        Eligible("seed-pro-id").Should().BeFalse("its OR branch includes an app_open eq 0 leaf (ZeroCount)");
    }

    private static bool Eligible(string name)
    {
        var tree = RuleParser.Parse(SeedSegments.All.Single(e => e.Name == name).Definition).Tree!;
        return RealtimeEligibility.Classify(tree).IsEligible;
    }
}
