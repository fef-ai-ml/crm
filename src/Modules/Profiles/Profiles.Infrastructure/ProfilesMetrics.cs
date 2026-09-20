using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Crm.Profiles.Infrastructure;

/// <summary>
/// Spec §8.2: crm_dlq_total{topic}, crm_consumer_lag and crm_publish_retries_total{topic}. The Prometheus exporter
/// appends `_total` to counters, so the counter instruments are named without it; gauges are exported under their
/// own name.
/// </summary>
public sealed class ProfilesMetrics : IDisposable
{
    public const string MeterName = "Crm.Profiles";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _deadLettered;
    private readonly Counter<long> _publishRetried;
    private readonly ConcurrentDictionary<(string Topic, int Partition), long> _lag = new();

    public ProfilesMetrics()
    {
        _deadLettered = _meter.CreateCounter<long>("crm_dlq", description: "Messages moved to events.dlq, by source topic.");
        _publishRetried = _meter.CreateCounter<long>("crm_publish_retries", description: "Failed produce attempts to a Worker output topic that are being retried in place.");
        _meter.CreateObservableGauge("crm_consumer_lag", ObserveLag, description: "High watermark minus consumer position, per assigned partition.");
    }

    public void DeadLettered(string topic) => _deadLettered.Add(1, new KeyValuePair<string, object?>("topic", topic));

    public void PublishRetried(string topic) => _publishRetried.Add(1, new KeyValuePair<string, object?>("topic", topic));

    public void SetLag(string topic, int partition, long lag) => _lag[(topic, partition)] = lag;

    /// <summary>A revoked partition is someone else's to report.</summary>
    public void ClearLag(string topic, int partition) => _lag.TryRemove((topic, partition), out _);

    public void Dispose() => _meter.Dispose();

    private IEnumerable<Measurement<long>> ObserveLag()
        => _lag.Select(entry => new Measurement<long>(
            entry.Value,
            new KeyValuePair<string, object?>("topic", entry.Key.Topic),
            new KeyValuePair<string, object?>("partition", entry.Key.Partition)));
}
