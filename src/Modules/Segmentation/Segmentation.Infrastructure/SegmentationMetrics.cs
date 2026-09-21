using System.Diagnostics.Metrics;
using Crm.Segmentation.Domain.Segments;

namespace Crm.Segmentation.Infrastructure;

/// <summary>task-7 brief: crm_segment_run_seconds{trigger,outcome}, the duration of one batch run from claim
/// (H4) to Finished or Failed. <see cref="MeterName"/> follows the same `Crm.&lt;module&gt;` convention as the
/// Profiles module's own metrics meter; both are captured by the `Crm.*` wildcard already registered with
/// OpenTelemetry (Crm.Hosting.Telemetry.OpenTelemetryExtensions), so no separate meter registration is needed.</summary>
public sealed class SegmentationMetrics : IDisposable
{
    public const string MeterName = "Crm.Segmentation";

    private readonly Meter _meter = new(MeterName);
    private readonly Histogram<double> _runSeconds;

    public SegmentationMetrics()
    {
        _runSeconds = _meter.CreateHistogram<double>(
            "crm_segment_run_seconds", unit: "s", description: "Duration of a batch segment run, from claim to Finished or Failed.");
    }

    public void RecordRunDuration(double seconds, SegmentRunTrigger trigger, string outcome)
        => _runSeconds.Record(
            seconds,
            new KeyValuePair<string, object?>("trigger", trigger.ToString().ToLowerInvariant()),
            new KeyValuePair<string, object?>("outcome", outcome));

    public void Dispose() => _meter.Dispose();
}
