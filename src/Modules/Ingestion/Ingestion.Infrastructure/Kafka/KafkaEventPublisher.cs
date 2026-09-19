using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using Crm.Contracts;
using Crm.Ingestion.Application;
using Crm.Ingestion.Application.Envelopes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crm.Ingestion.Infrastructure.Kafka;

/// <summary>
/// events.raw producer: acks=all + idempotence, and every call awaits its delivery report, so a 202 means the record
/// is on the log. A record that is not acknowledged within MessageTimeoutMs fails the call; librdkafka drops it, so
/// the API never holds events in memory for later (spec §4.3).
/// </summary>
internal sealed class KafkaEventPublisher : IEventPublisher, IDisposable
{
    private static readonly ActivitySource Source = new("Crm.Ingestion");
    private readonly Lazy<IProducer<string, byte[]>> _producer;
    private readonly ILogger<KafkaEventPublisher> _logger;

    public KafkaEventPublisher(IOptions<KafkaProducerOptions> options, ILogger<KafkaEventPublisher> logger)
    {
        _logger = logger;
        var settings = options.Value;
        // Lazy: the host is also built for OpenAPI export and for the Worker, where nothing is ever published.
        _producer = new Lazy<IProducer<string, byte[]>>(() => new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = settings.BootstrapServers,
            ClientId = "crm-api",
            Acks = Acks.All,
            EnableIdempotence = true,
            LingerMs = settings.Producer.LingerMs,
            MessageTimeoutMs = settings.Producer.MessageTimeoutMs,
        }).Build());
    }

    public async Task PublishAsync(RawMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var activity = Source.StartActivity($"{KafkaTopics.EventsRaw} publish", ActivityKind.Producer);

        var headers = new Headers();
        foreach (var (name, value) in message.Headers)
        {
            headers.Add(name, Encoding.UTF8.GetBytes(value));
        }

        if (Activity.Current?.Id is { } traceParent)
        {
            headers.Add(KafkaHeaders.TraceParent, Encoding.UTF8.GetBytes(traceParent));
        }

        try
        {
            await _producer.Value.ProduceAsync(
                KafkaTopics.EventsRaw,
                new Message<string, byte[]> { Key = message.Key, Value = message.Value, Headers = headers },
                cancellationToken);
        }
        catch (KafkaException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Error.Reason);
            _logger.LogWarning(ex, "Publishing {MessageType} {EventId} to {Topic} failed: {Reason}", message.MessageType, message.EventId, KafkaTopics.EventsRaw, ex.Error.Reason);
            throw new EventPublishException($"Kafka did not acknowledge {message.MessageType} {message.EventId}: {ex.Error.Reason}", ex);
        }
    }

    public void Dispose()
    {
        if (_producer.IsValueCreated)
        {
            _producer.Value.Flush(TimeSpan.FromSeconds(5));
            _producer.Value.Dispose();
        }
    }
}
