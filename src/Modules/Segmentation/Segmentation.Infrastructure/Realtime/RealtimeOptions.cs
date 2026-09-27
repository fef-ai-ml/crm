using Crm.Contracts;

namespace Crm.Segmentation.Infrastructure.Realtime;

/// <summary>Segmentation:Realtime — the `events.processed` consumer (ruling I8) and its segment cache (I6).</summary>
public sealed class RealtimeOptions
{
    public const string SectionName = "Segmentation:Realtime";

    private static readonly int[] DefaultRetryDelaysMs = [200, 2000];

    /// <summary>False keeps the services registered but never subscribes.</summary>
    public bool Enabled { get; set; } = true;

    public string GroupId { get; set; } = KafkaTopics.SegmentationConsumerGroup;

    /// <summary>Consumers in this process; Kafka spreads the 12 partitions over them, each processes sequentially.</summary>
    public int Instances { get; set; } = 4;

    /// <summary>Waits between attempts of a SQL step (I9); attempts = length + 1. Empty means 200 ms, 2 s — not the
    /// initial value, because the configuration binder appends to a non-empty list instead of replacing it.</summary>
    public IList<int> RetryDelaysMs { get; set; } = [];

    /// <summary>Pause before a consumer is rebuilt after a failure outside message processing.</summary>
    public int RestartDelayMs { get; set; } = 1000;

    /// <summary>How long a tenant's cached segment list lives without a `segments:changed` message (I6).</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Filled from Kafka:BootstrapServers by the registration.</summary>
    public string BootstrapServers { get; set; } = string.Empty;

    public IReadOnlyList<TimeSpan> RetryDelays
        => (RetryDelaysMs.Count > 0 ? RetryDelaysMs : DefaultRetryDelaysMs).Select(ms => TimeSpan.FromMilliseconds(ms)).ToList();
}
