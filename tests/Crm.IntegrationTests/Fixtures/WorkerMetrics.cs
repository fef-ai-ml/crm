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
}
