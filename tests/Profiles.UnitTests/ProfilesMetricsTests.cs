using System.Diagnostics.Metrics;
using Crm.Profiles.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class ProfilesMetricsTests
{
    [Fact]
    public void Dead_letters_are_counted_per_source_topic_and_lag_is_observed_per_partition()
    {
        using var metrics = new ProfilesMetrics();
        var measurements = new List<(string Instrument, long Value, string Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ProfilesMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, string.Join(",", tags.ToArray().Select(t => $"{t.Key}={t.Value}")))));
        listener.Start();

        metrics.DeadLettered("events.raw");
        metrics.SetLag("events.raw", 3, 17);
        metrics.SetLag("events.raw", 4, 0);
        metrics.ClearLag("events.raw", 4);
        listener.RecordObservableInstruments();

        // The Prometheus exporter appends _total to counters, so the instrument is named without it.
        measurements.Should().Contain(("crm_dlq", 1L, "topic=events.raw"));
        measurements.Should().Contain(("crm_consumer_lag", 17L, "topic=events.raw,partition=3"));
        measurements.Should().NotContain(m => m.Tags.EndsWith("partition=4", StringComparison.Ordinal));
    }
}
