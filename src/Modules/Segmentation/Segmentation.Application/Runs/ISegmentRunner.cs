namespace Crm.Segmentation.Application.Runs;

/// <summary>
/// Materialises one queued run (task-7 brief, H6/H7/H8): insert members, diff against the previous run, swap the
/// current run, prune old runs — one transaction. Never throws for a failure of the run itself: on any error it
/// rolls back, records the run as Failed with the exception message, and returns <see langword="false"/>, so the
/// caller (<c>SegmentRunWorker</c>) can tag its duration metric and move on to the next queued run.
/// </summary>
public interface ISegmentRunner
{
    /// <summary>Runs <paramref name="runId"/>, which the caller has already claimed (H4: status Queued → Running)
    /// in the same tenant scope. Returns <see langword="true"/> if the run finished, <see langword="false"/> if it
    /// failed.</summary>
    Task<bool> RunAsync(Guid runId, CancellationToken cancellationToken);
}
