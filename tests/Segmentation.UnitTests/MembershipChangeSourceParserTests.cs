using Crm.Segmentation.Domain.Segments;
using FluentAssertions;
using Xunit;

namespace Crm.Segmentation.UnitTests;

/// <summary>Plan I, ruling I15: `?source=` on GET /segments/{id}/changes accepts the two source names only,
/// case-insensitively, with the same totality guard as <see cref="SegmentStatusParser"/>.</summary>
public sealed class MembershipChangeSourceParserTests
{
    [Theory]
    [InlineData("Batch", MembershipChangeSource.Batch)]
    [InlineData("batch", MembershipChangeSource.Batch)]
    [InlineData("Realtime", MembershipChangeSource.Realtime)]
    [InlineData("REALTIME", MembershipChangeSource.Realtime)]
    public void Accepts_the_two_names_case_insensitively(string value, MembershipChangeSource expected)
    {
        MembershipChangeSourceParser.TryParse(value, out var source).Should().BeTrue();
        source.Should().Be(expected);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("Batch,Realtime")]
    [InlineData("real-time")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_anything_else(string? value)
        => MembershipChangeSourceParser.TryParse(value, out _).Should().BeFalse();
}
