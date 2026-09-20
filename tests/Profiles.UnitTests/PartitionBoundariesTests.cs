using Crm.Profiles.Infrastructure.Maintenance;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class PartitionBoundariesTests
{
    [Fact]
    public void Upcoming_is_the_first_instant_of_this_month_and_of_the_months_ahead()
    {
        var boundaries = PartitionBoundaries.Upcoming(new DateTime(2027, 11, 20, 13, 45, 0, DateTimeKind.Utc), monthsAhead: 2);

        boundaries.Should().Equal(
            new DateTime(2027, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 12, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }
}
