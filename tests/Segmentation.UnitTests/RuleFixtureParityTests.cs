using Crm.Segmentation.Domain.Rules;
using FluentAssertions;
using Xunit;

namespace Crm.Segmentation.UnitTests;

/// <summary>CT-002, the C# half: the Zod half is a later Web plan.</summary>
[Trait("Category", "Contract")]
public class RuleFixtureParityTests
{
    [Theory]
    [MemberData(nameof(Fixtures.Valid), MemberType = typeof(Fixtures))]
    public void CT002_every_valid_fixture_parses_with_no_errors(string name)
    {
        var (definition, _) = Fixtures.Load(name);

        var result = RuleParser.Parse(definition);

        result.Errors.Should().BeEmpty($"fixture '{name}' is listed as valid");
    }

    [Theory]
    [MemberData(nameof(Fixtures.Invalid), MemberType = typeof(Fixtures))]
    public void CT002_every_invalid_fixture_fails_at_its_expected_first_error_path(string name)
    {
        var (definition, expectedErrorPath) = Fixtures.Load(name);

        var result = RuleParser.Parse(definition);

        result.IsValid.Should().BeFalse($"fixture '{name}' is listed as invalid");
        result.Errors[0].Path.Should().Be(expectedErrorPath, $"fixture '{name}'s first error path");
    }

    [Fact]
    public void CT002_the_fixture_directories_contain_exactly_the_documented_files()
    {
        Directory.GetFiles(Path.Combine(Fixtures.RulesRoot(), "valid"), "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Should().BeEquivalentTo(Fixtures.ValidFileNames);

        Directory.GetFiles(Path.Combine(Fixtures.RulesRoot(), "invalid"), "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Should().BeEquivalentTo(Fixtures.InvalidFileNames);
    }
}
