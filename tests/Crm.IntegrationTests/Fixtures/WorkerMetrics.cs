using System.Globalization;

namespace Crm.IntegrationTests.Fixtures;

public static class WorkerMetrics
{
    /// <summary>Current value of crm_dlq_total{topic="…"} on the Worker's /metrics; 0 while the series does not exist yet.</summary>
    public static async Task<double> DeadLetteredAsync(HttpClient worker, string topic)
    {
        ArgumentNullException.ThrowIfNull(worker);
        var body = await worker.GetStringAsync(new Uri("/metrics", UriKind.Relative));
        var line = body.Split('\n').FirstOrDefault(l =>
            l.StartsWith("crm_dlq_total{", StringComparison.Ordinal) && l.Contains($"topic=\"{topic}\"", StringComparison.Ordinal));
        if (line is null)
        {
            return 0;
        }

        // `name{labels} value [timestamp]` — the value is the first token after the closing brace.
        var afterLabels = line[(line.LastIndexOf('}') + 1)..].Trim();
        return double.Parse(afterLabels.Split(' ')[0], CultureInfo.InvariantCulture);
    }

    /// <summary>Sum of `crm_segment_run_seconds_count{trigger,outcome}` across every label combination — a batch
    /// run histogram (task-7 brief), so IT-016's proof is just "at least one observation happened".</summary>
    public static async Task<double> SegmentRunSecondsCountAsync(HttpClient worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        var body = await worker.GetStringAsync(new Uri("/metrics", UriKind.Relative));
        return body.Split('\n')
            .Where(l => l.StartsWith("crm_segment_run_seconds_count{", StringComparison.Ordinal))
            .Sum(l => double.Parse(l[(l.LastIndexOf('}') + 1)..].Trim().Split(' ')[0], CultureInfo.InvariantCulture));
    }

    /// <summary>B3 (I-3): same series, restricted to samples carrying `outcome="failed"` — proves a failed run
    /// leaves a distinctly-tagged observation (both `trigger` and `outcome`), not just an increment somewhere in
    /// the untagged total.</summary>
    public static async Task<double> SegmentRunSecondsFailedCountAsync(HttpClient worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        var body = await worker.GetStringAsync(new Uri("/metrics", UriKind.Relative));
        return body.Split('\n')
            .Where(l => l.StartsWith("crm_segment_run_seconds_count{", StringComparison.Ordinal)
                && l.Contains("outcome=\"failed\"", StringComparison.Ordinal))
            .Sum(l => double.Parse(l[(l.LastIndexOf('}') + 1)..].Trim().Split(' ')[0], CultureInfo.InvariantCulture));
    }
}
