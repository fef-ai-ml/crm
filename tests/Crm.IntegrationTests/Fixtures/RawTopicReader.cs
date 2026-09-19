using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Crm.Contracts;

namespace Crm.IntegrationTests.Fixtures;

public sealed record RawRecord(string Key, string MessageType, JsonElement Payload, IReadOnlyDictionary<string, string> Headers);

public static class RawTopicReader
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Reads events.raw from the beginning with a throw-away group and returns the records of one tenant (tests create
    /// a tenant each, so this isolates them). Waits until <paramref name="expected"/> records arrived and then a quiet
    /// period longer, so "exactly N" assertions can see an unexpected extra record.
    /// </summary>
    public static Task<IReadOnlyList<RawRecord>> ReadTenantAsync(string bootstrapServers, Guid tenantId, int expected, TimeSpan timeout)
        => Task.Run<IReadOnlyList<RawRecord>>(() =>
        {
            using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
            {
                BootstrapServers = bootstrapServers,
                GroupId = $"it-{Guid.NewGuid():N}",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
            }).Build();
            consumer.Subscribe(KafkaTopics.EventsRaw);

            var tenant = tenantId.ToString("N");
            var records = new List<RawRecord>();
            var deadline = DateTime.UtcNow + timeout;
            DateTime? quietUntil = null;
            while (DateTime.UtcNow < deadline && (quietUntil is null || DateTime.UtcNow < quietUntil))
            {
                var result = consumer.Consume(TimeSpan.FromMilliseconds(250));
                if (result?.Message is null)
                {
                    continue;
                }

                var headers = result.Message.Headers.ToDictionary(h => h.Key, h => Encoding.UTF8.GetString(h.GetValueBytes()), StringComparer.Ordinal);
                if (!headers.TryGetValue(KafkaHeaders.TenantId, out var owner) || owner != tenant)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(result.Message.Value);
                records.Add(new RawRecord(result.Message.Key, headers[KafkaHeaders.MessageType], document.RootElement.Clone(), headers));
                if (records.Count >= expected)
                {
                    quietUntil = DateTime.UtcNow + Quiet;
                }
            }

            consumer.Close();
            return records;
        });
}
