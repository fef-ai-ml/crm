using System.Diagnostics.Metrics;
using Crm.Segmentation.Domain.Segments;
using Crm.Segmentation.Infrastructure.Realtime;

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
    private readonly Histogram<double> _realtimeEvalSeconds;
    private readonly Counter<long> _realtimeSkipped;

    public SegmentationMetrics()
    {
        _runSeconds = _meter.CreateHistogram<double>(
            "crm_segment_run_seconds", unit: "s", description: "Duration of a batch segment run, from claim to Finished or Failed.");
        _realtimeEvalSeconds = _meter.CreateHistogram<double>(
            "crm_realtime_eval_seconds", unit: "s", description: "Real-time handling of one events.processed message, from decode to the change rows being saved.");
        _realtimeSkipped = _meter.CreateCounter<long>(
            "crm_realtime_skipped_total", description: "events.processed messages the real-time path skipped (wholly or partly), by reason: decode, redis, sql.");
    }

    public void RecordRunDuration(double seconds, SegmentRunTrigger trigger, string outcome)
        => _runSeconds.Record(
            seconds,
            new KeyValuePair<string, object?>("trigger", trigger.ToString().ToLowerInvariant()),
            new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>Plan I, ruling I10: tag `type` = event_tracked | profile_updated | profile_merged.</summary>
    internal void RecordRealtimeEval(double seconds, RealtimeMessage message)
        => _realtimeEvalSeconds.Record(seconds, new KeyValuePair<string, object?>("type", message switch
        {
            RealtimeMessage.Tracked => "event_tracked",
            RealtimeMessage.Updated => "profile_updated",
            _ => "profile_merged",
        }));

    /// <summary>Ruling I9: `reason` = decode | redis | sql.</summary>
    public void RealtimeSkipped(string reason) => _realtimeSkipped.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void Dispose() => _meter.Dispose();
}
