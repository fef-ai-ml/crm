using Crm.Profiles.Application.Queries;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests.Queries;

public sealed class TimelineFilterTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 21, 12, 0, 0, TimeSpan.FromHours(7));

    [Fact]
    public void Absent_filters_are_valid()
    {
        var filter = TimelineFilter.Create(null, null, null).Value;
        filter.Should().Be(new TimelineFilter(null, null, null));
    }

    [Fact]
    public void Offsets_are_converted_to_utc()
    {
        var filter = TimelineFilter.Create(Noon, Noon.AddHours(1), "purchase").Value;

        filter.FromUtc.Should().Be(new DateTime(2026, 9, 21, 5, 0, 0, DateTimeKind.Utc));
        filter.ToUtc.Should().Be(new DateTime(2026, 9, 21, 6, 0, 0, DateTimeKind.Utc));
        filter.Name.Should().Be("purchase");
    }

    [Theory]
    [InlineData("bad name!")]
    [InlineData("purchase\n")]
    [InlineData("")]
    public void Name_must_match_the_event_name_rule(string name)
    {
        var result = TimelineFilter.Create(null, null, name);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("profiles.invalid_filter");
        result.Error.Fields.Should().ContainKey("name");
    }

    [Fact]
    public void From_must_be_before_to()
    {
        var result = TimelineFilter.Create(Noon, Noon, null);

        result.IsFailure.Should().BeTrue();
        result.Error!.Fields.Should().ContainKey("from");
    }
}
