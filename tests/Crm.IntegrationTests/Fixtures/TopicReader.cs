using System.Text;
using Confluent.Kafka;
using Crm.Contracts;

namespace Crm.IntegrationTests.Fixtures;

public sealed record TopicRecord(string Topic, int Partition, long Offset, string? Key, byte[] Value, IReadOnlyDictionary<string, string> Headers)
{
    public string MessageType => Headers.GetValueOrDefault(KafkaHeaders.MessageType, string.Empty);
}

/// <summary>Like RawTopicReader, for any topic and without parsing the value (a dead letter need not be JSON).</summary>
public static class TopicReader
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

    public static Task<IReadOnlyList<TopicRecord>> ReadTenantAsync(string bootstrapServers, string topic, Guid tenantId, int expected, TimeSpan timeout)
        => Task.Run<IReadOnlyList<TopicRecord>>(() =>
        {
            using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
            {
                BootstrapServers = bootstrapServers,
                GroupId = $"it-{Guid.NewGuid():N}",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
            }).Build();
            consumer.Subscribe(topic);

            var tenant = tenantId.ToString("N");
            var records = new List<TopicRecord>();
            var deadline = DateTime.UtcNow + timeout;
            DateTime? quietUntil = null;
            while (DateTime.UtcNow < deadline && (quietUntil is null || DateTime.UtcNow < quietUntil))
            {
                var result = consumer.Consume(TimeSpan.FromMilliseconds(250));
                if (result?.Message is null)
                {
                    continue;
                }

                var headers = result.Message.Headers.ToDictionary(h => h.Key, h => Encoding.UTF8.GetString(h.GetValueBytes() ?? []), StringComparer.Ordinal);
                if (!headers.TryGetValue(KafkaHeaders.TenantId, out var owner) || owner != tenant)
                {
                    continue;
                }

                records.Add(new TopicRecord(result.Topic, result.Partition.Value, result.Offset.Value, result.Message.Key, result.Message.Value ?? [], headers));
                if (records.Count >= expected)
                {
                    quietUntil = DateTime.UtcNow + Quiet;
                }
            }

            consumer.Close();
            return records;
        });
}
