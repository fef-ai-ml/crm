using System.Text;
using Confluent.Kafka;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>Raw access to the broker for tests that must produce a record the API would never produce, or look at a group's offsets.</summary>
public static class KafkaTestClient
{
    public static async Task<TopicPartitionOffset> ProduceAsync(string bootstrapServers, string topic, string? key, byte[] value, IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = bootstrapServers, Acks = Acks.All }).Build();
        var kafkaHeaders = new Headers();
        foreach (var (name, text) in headers)
        {
            kafkaHeaders.Add(name, Encoding.UTF8.GetBytes(text));
        }

        var report = await producer.ProduceAsync(topic, new Message<string, byte[]> { Key = key!, Value = value, Headers = kafkaHeaders });
        return report.TopicPartitionOffset;
    }

    /// <summary>The next offset the group will read on the partition; -1001 (Offset.Unset) when it never committed there. Does not join the group.</summary>
    public static long CommittedOffset(string bootstrapServers, string groupId, TopicPartition partition)
    {
        using var consumer = new ConsumerBuilder<Ignore, Ignore>(new ConsumerConfig { BootstrapServers = bootstrapServers, GroupId = groupId, EnableAutoCommit = false }).Build();
        return consumer.Committed([partition], TimeSpan.FromSeconds(10)).Single().Offset.Value;
    }
}
