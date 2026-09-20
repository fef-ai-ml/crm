using System.Text;
using Confluent.Kafka;
using Crm.Profiles.Infrastructure.Consumers;
using Microsoft.Extensions.Options;

namespace Crm.Profiles.Infrastructure.Kafka;

/// <summary>One idempotent acks=all producer for events.processed and events.dlq. Built on first use; a construction failure is not cached.</summary>
internal sealed class WorkerProducer(IOptions<ProfilesConsumerOptions> options) : IDisposable
{
    private readonly Lock _gate = new();
    private IProducer<string, byte[]>? _producer;

    public Task ProduceAsync(string topic, string? key, byte[] value, IEnumerable<KeyValuePair<string, string>> headers, CancellationToken cancellationToken)
    {
        var kafkaHeaders = new Headers();
        foreach (var (name, text) in headers)
        {
            kafkaHeaders.Add(name, Encoding.UTF8.GetBytes(text));
        }

        // Key is null for a dead letter whose source had none; the serializer writes a null key.
        return Get().ProduceAsync(topic, new Message<string, byte[]> { Key = key!, Value = value, Headers = kafkaHeaders }, cancellationToken);
    }

    public void Dispose()
    {
        if (_producer is { } producer)
        {
            producer.Flush(TimeSpan.FromSeconds(5));
            producer.Dispose();
        }
    }

    private IProducer<string, byte[]> Get()
    {
        if (_producer is { } existing)
        {
            return existing;
        }

        lock (_gate)
        {
            return _producer ??= new ProducerBuilder<string, byte[]>(new ProducerConfig
            {
                BootstrapServers = options.Value.BootstrapServers,
                ClientId = "crm-worker",
                Acks = Acks.All,
                EnableIdempotence = true,
                LingerMs = 5,
            }).Build();
        }
    }
}
