using System.Diagnostics.Metrics;

namespace Crm.Ingestion.Application;

/// <summary>
/// Spec §8 names the Prometheus series crm_ingest_events_total and crm_ingest_rejected_total. The OpenTelemetry
/// Prometheus exporter appends `_total` to counters, so the instruments are named without it.
/// </summary>
public sealed class IngestMetrics : IDisposable
{
    public const string MeterName = "Crm.Ingestion";
    public const string TypeTrack = "track";
    public const string TypeIdentify = "identify";
    public const string ReasonValidation = "validation";
    public const string ReasonUnavailable = "unavailable";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _accepted;
    private readonly Counter<long> _rejected;

    public IngestMetrics()
    {
        _accepted = _meter.CreateCounter<long>("crm_ingest_events", description: "Events acknowledged by Kafka, by type.");
        _rejected = _meter.CreateCounter<long>("crm_ingest_rejected", description: "Events or requests not accepted, by reason.");
    }

    public void Accepted(string type, long count = 1) => _accepted.Add(count, new KeyValuePair<string, object?>("type", type));

    public void Rejected(string reason, long count = 1) => _rejected.Add(count, new KeyValuePair<string, object?>("reason", reason));

    public void Dispose() => _meter.Dispose();
}
