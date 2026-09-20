using Crm.Profiles.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class EventTimeTests
{
    private static readonly DateTime Received = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Occurred_at_is_truncated_to_milliseconds_so_it_round_trips_through_datetime2_3()
    {
        var occurred = Received.AddTicks(1_234_567); // 123.4567 ms

        var normalized = EventTime.Normalize(occurred, Received);

        normalized.Should().Be(Received.AddMilliseconds(123));
        normalized.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Occurred_at_is_clamped_to_seven_days_around_received_at()
    {
        EventTime.Normalize(Received.AddDays(-30), Received).Should().Be(Received.AddDays(-7));
        EventTime.Normalize(Received.AddDays(30), Received).Should().Be(Received.AddDays(7));
        EventTime.Normalize(Received.AddDays(-7), Received).Should().Be(Received.AddDays(-7));
    }

    [Fact]
    public void Normalizing_twice_is_the_same_as_once_so_redelivery_keeps_the_idempotency_key()
    {
        var once = EventTime.Normalize(Received.AddDays(-30).AddTicks(7), Received);
        EventTime.Normalize(once, Received).Should().Be(once);
    }
}
