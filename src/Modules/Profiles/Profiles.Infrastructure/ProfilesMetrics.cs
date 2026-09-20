using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Crm.Profiles.Infrastructure;

/// <summary>
/// Spec §8.2: crm_dlq_total{topic} and crm_consumer_lag. The Prometheus exporter appends `_total` to counters, so the
/// counter instrument is `crm_dlq`; gauges are exported under their own name.
/// </summary>
public sealed class ProfilesMetrics : IDisposable
{
    public const string MeterName = "Crm.Profiles";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _deadLettered;
    private readonly ConcurrentDictionary<(string Topic, int Partition), long> _lag = new();

    public ProfilesMetrics()
    {
        _deadLettered = _meter.CreateCounter<long>("crm_dlq", description: "Messages moved to events.dlq, by source topic.");
        _meter.CreateObservableGauge("crm_consumer_lag", ObserveLag, description: "High watermark minus consumer position, per assigned partition.");
    }

    public void DeadLettered(string topic) => _deadLettered.Add(1, new KeyValuePair<string, object?>("topic", topic));

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
