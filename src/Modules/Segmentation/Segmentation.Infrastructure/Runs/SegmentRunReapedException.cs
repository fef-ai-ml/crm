namespace Crm.Segmentation.Infrastructure.Runs;

/// <summary>H29 (fix wave B2/I-2): thrown by <see cref="SegmentRunner"/> when its finish update finds the run's
/// row no longer `Running` — the reaper (or another Worker instance) declared it abandoned while this run's own
/// transaction was still open. Caught by <see cref="SegmentRunner.RunAsync"/>'s own catch block, which rolls the
/// transaction back (discarding the member insert, both diffs and the <c>segments.Segments</c> swap) and leaves
/// the reaper's `Failed`/`'abandoned'` status alone — a reaped run must never resurrect as `Finished`.</summary>
public sealed class SegmentRunReapedException : Exception
{
    public SegmentRunReapedException()
    {
    }

    public SegmentRunReapedException(string message)
        : base(message)
    {
    }

    public SegmentRunReapedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
