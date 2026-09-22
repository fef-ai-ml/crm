using Crm.Segmentation.Infrastructure.Runs;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Segments;

/// <summary>B4 (I-4/M-9): pure fallback logic, no container stack needed — placed here rather than in
/// Segmentation.UnitTests because that project references only Segmentation.Application, not Infrastructure
/// (where <see cref="SegmentationOptionsDefaults"/> lives, next to the options types it defends). Not decorated
/// with <c>[Collection(StackCollectionDefinition.Name)]</c>, so it never touches the stack fixture.</summary>
[Trait("Category", "Unit")]
public sealed class SegmentationOptionsDefaultsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Poll_interval_at_or_below_zero_falls_back_to_the_default(int seconds)
    {
        SegmentationOptionsDefaults.EffectivePollInterval(TimeSpan.FromSeconds(seconds))
            .Should().Be(SegmentRunOptions.DefaultPollInterval);
    }

    [Fact]
    public void A_positive_poll_interval_is_kept_as_is()
    {
        var configured = TimeSpan.FromSeconds(42);
        SegmentationOptionsDefaults.EffectivePollInterval(configured).Should().Be(configured);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Command_timeout_at_or_below_zero_falls_back_to_the_default(int seconds)
    {
        // I-4: 0 means "no timeout" to ADO.NET, and a negative value throws per statement — neither is what a
        // misconfigured value meant.
        SegmentationOptionsDefaults.EffectiveCommandTimeoutSeconds(seconds)
            .Should().Be(SegmentRunOptions.DefaultCommandTimeoutSeconds);
    }

    [Fact]
    public void A_positive_command_timeout_is_kept_as_is()
    {
        SegmentationOptionsDefaults.EffectiveCommandTimeoutSeconds(45).Should().Be(45);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Schedule_interval_at_or_below_zero_falls_back_to_the_default(int hours)
    {
        // I-4: PeriodicTimer throws ArgumentOutOfRangeException for TimeSpan.Zero or negative, escaping
        // SegmentScheduler.ExecuteAsync before its own try/catch and bringing down the whole Worker host.
        SegmentationOptionsDefaults.EffectiveScheduleInterval(TimeSpan.FromHours(hours))
            .Should().Be(SegmentScheduleOptions.DefaultInterval);
    }

    [Fact]
    public void A_positive_schedule_interval_is_kept_as_is()
    {
        var configured = TimeSpan.FromHours(2);
        SegmentationOptionsDefaults.EffectiveScheduleInterval(configured).Should().Be(configured);
    }

    [Fact]
    public void Reap_cutoff_never_drops_below_the_ten_minute_floor()
    {
        // Default command timeout (300 s) × 8 = 2400 s (40 min), well above the floor, so this only exercises
        // the floor with a tiny configured timeout.
        SegmentationOptionsDefaults.ReapCutoff(1).Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Reap_cutoff_scales_with_the_command_timeout_once_it_exceeds_the_floor()
    {
        // H29: a run issues ~8 statements, so its wall-clock ceiling is roughly 8x the per-statement budget.
        SegmentationOptionsDefaults.ReapCutoff(300).Should().Be(TimeSpan.FromSeconds(2400));
    }
}
