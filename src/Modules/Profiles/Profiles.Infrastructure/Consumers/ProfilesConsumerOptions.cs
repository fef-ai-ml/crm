using Crm.Contracts;

namespace Crm.Profiles.Infrastructure.Consumers;

public sealed class ProfilesConsumerOptions
{
    public const string SectionName = "Profiles:Consumer";

    private static readonly int[] DefaultRetryDelaysMs = [200, 2000];

    /// <summary>False keeps the services registered but never subscribes (tooling, tests that only need the pipeline).</summary>
    public bool Enabled { get; set; } = true;

    public string GroupId { get; set; } = KafkaTopics.ProfilesConsumerGroup;

    /// <summary>Consumers in this process. Kafka spreads the partitions over them; each one processes sequentially (ruling E10).</summary>
    public int Instances { get; set; } = 4;

    /// <summary>
    /// Waits between attempts; attempts = length + 1. Empty means the spec default (200 ms, 2 s). The default is not
    /// the initial value because the configuration binder appends to a non-empty array instead of replacing it.
    /// </summary>
    public IList<int> RetryDelaysMs { get; set; } = [];

    /// <summary>Pause before a consumer is rebuilt after a failure outside message processing (ruling E9).</summary>
    public int RestartDelayMs { get; set; } = 1000;

    /// <summary>Filled from Kafka:BootstrapServers by the registration.</summary>
    public string BootstrapServers { get; set; } = string.Empty;

    public IReadOnlyList<TimeSpan> RetryDelays
        => (RetryDelaysMs.Count > 0 ? RetryDelaysMs : DefaultRetryDelaysMs).Select(ms => TimeSpan.FromMilliseconds(ms)).ToList();
}
