using Crm.Segmentation.Domain.Segments;
using FluentAssertions;
using Xunit;

namespace Crm.Segmentation.UnitTests;

/// <summary>I-1/M-1: `Enum.TryParse` alone accepts the string form of any integer and, since
/// <see cref="SegmentStatus"/> is not a `[Flags]` enum, a comma-joined list of names as a bitwise OR.
/// `SegmentStatusParser` must be total over the three names only.</summary>
public sealed class SegmentStatusParserTests
{
    [Theory]
    [InlineData("Draft", SegmentStatus.Draft)]
    [InlineData("draft", SegmentStatus.Draft)]
    [InlineData("DRAFT", SegmentStatus.Draft)]
    [InlineData("Active", SegmentStatus.Active)]
    [InlineData("Archived", SegmentStatus.Archived)]
    public void Accepts_the_three_names_case_insensitively(string value, SegmentStatus expected)
    {
        SegmentStatusParser.TryParse(value, out var status).Should().BeTrue();
        status.Should().Be(expected);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("-1")]
    public void Rejects_numeric_strings_even_when_they_are_a_defined_enum_value(string value)
    {
        SegmentStatusParser.TryParse(value, out _).Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_comma_joined_bitwise_or_of_names()
    {
        SegmentStatusParser.TryParse("Draft,Active", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("bogus")]
    [InlineData(null)]
    public void Rejects_empty_bogus_and_null(string? value)
    {
        SegmentStatusParser.TryParse(value, out _).Should().BeFalse();
    }
}
