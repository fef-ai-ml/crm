using Crm.Tenancy.Infrastructure.Auth;
using FluentAssertions;
using Xunit;

namespace Crm.Tenancy.UnitTests;

public class ApiKeyUsageTrackerTests
{
    private static readonly DateTime T0 = new(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_prefix_is_touched_at_most_once_per_minute()
    {
        var tracker = new ApiKeyUsageTracker();

        tracker.ShouldTouch("ABCDEFGH", T0).Should().BeTrue();
        tracker.ShouldTouch("ABCDEFGH", T0.AddSeconds(59)).Should().BeFalse();
        tracker.ShouldTouch("ABCDEFGH", T0.AddSeconds(60)).Should().BeTrue();
        tracker.ShouldTouch("ZZZZZZZZ", T0.AddSeconds(1)).Should().BeTrue("prefixes are tracked independently");
    }

    [Fact]
    public void Concurrent_callers_get_exactly_one_touch()
    {
        var tracker = new ApiKeyUsageTracker();
        var granted = 0;

        Parallel.For(0, 200, _ =>
        {
            if (tracker.ShouldTouch("ABCDEFGH", T0))
            {
                Interlocked.Increment(ref granted);
            }
        });

        granted.Should().Be(1);
    }
}
