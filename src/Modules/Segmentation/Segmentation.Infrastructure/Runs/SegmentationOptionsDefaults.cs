namespace Crm.Segmentation.Infrastructure.Runs;

/// <summary>
/// Pure fallback logic for the `Segmentation:*` options that would otherwise let a bad configuration value
/// crash the Worker host (I-4): <see cref="SegmentScheduleOptions.Interval"/> ≤ <c>TimeSpan.Zero</c> throws
/// inside <see cref="SegmentScheduler"/>'s <c>PeriodicTimer</c> before its own <c>try</c>/<c>catch</c> is even
/// reached, and .NET's default <c>BackgroundServiceExceptionBehavior.StopHost</c> then brings down the whole
/// Worker — including the Profiles consumers. <see cref="SegmentRunOptions.CommandTimeoutSeconds"/> ≤ 0 means
/// "no timeout" in ADO.NET (0) or throws per statement (negative), neither of which is what a typo meant.
/// <see cref="SegmentRunOptions.PollInterval"/> ≤ <c>TimeSpan.Zero</c> already had this treatment (fix round 1);
/// it moved here so all three fallbacks are one pure, unit-testable place — options in, effective value out —
/// instead of scattered `PostConfigure` bodies.
/// </summary>
public static class SegmentationOptionsDefaults
{
    /// <summary>H29 (fix wave B2): a run issues about six statements (the member insert, two `EXCEPT` inserts,
    /// two updates and the prune) inside <see cref="SegmentRunOptions.CommandTimeoutSeconds"/> each, so its
    /// wall-clock ceiling is roughly that many times the per-statement budget, not the budget itself. The reap
    /// window must clear that ceiling with margin, and never drop below the original 10-minute floor.</summary>
    private const int StatementsPerRun = 8;

    private static readonly TimeSpan MinimumReapWindow = TimeSpan.FromMinutes(10);

    public static TimeSpan EffectivePollInterval(TimeSpan configured) =>
        configured <= TimeSpan.Zero ? SegmentRunOptions.DefaultPollInterval : configured;

    public static int EffectiveCommandTimeoutSeconds(int configured) =>
        configured <= 0 ? SegmentRunOptions.DefaultCommandTimeoutSeconds : configured;

    public static TimeSpan EffectiveScheduleInterval(TimeSpan configured) =>
        configured <= TimeSpan.Zero ? SegmentScheduleOptions.DefaultInterval : configured;

    /// <summary>H29: <c>max(10 min, 8 × the effective command timeout)</c>. Takes the already-effective command
    /// timeout (post <see cref="EffectiveCommandTimeoutSeconds"/>), so a caller never needs to apply that
    /// fallback twice.</summary>
    public static TimeSpan ReapCutoff(int effectiveCommandTimeoutSeconds)
    {
        var budget = TimeSpan.FromSeconds((double)effectiveCommandTimeoutSeconds * StatementsPerRun);
        return budget > MinimumReapWindow ? budget : MinimumReapWindow;
    }
}
