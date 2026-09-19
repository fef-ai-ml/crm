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
/// the API never holds events in memory for later (spec §4.3). If the caller's request is cancelled instead,
/// <see cref="PublishAsync"/> throws <see cref="OperationCanceledException"/>, but the record MAY still have been
/// appended: cancelling the awaited task does not withdraw the message from librdkafka's send queue, so the
/// delivery cannot be recalled. Callers must not assume "exception ⇒ absent" for either exception.
/// </summary>
internal sealed class KafkaEventPublisher : IEventPublisher, IDisposable
{
    private static readonly ActivitySource Source = new("Crm.Ingestion");
    private readonly Lock _gate = new();
    private readonly IOptions<KafkaProducerOptions> _options;
    private readonly ILogger<KafkaEventPublisher> _logger;
    private readonly Func<ProducerConfig, IProducer<string, byte[]>> _factory;
    private IProducer<string, byte[]>? _producer;

    // The host is also built for OpenAPI export and for the Worker, where nothing is ever published, so the
    // producer is built lazily. Unlike Lazy<T>, a construction failure here is never cached: the next call
    // retries from scratch instead of rethrowing the same exception forever.
    public KafkaEventPublisher(IOptions<KafkaProducerOptions> options, ILogger<KafkaEventPublisher> logger)
        : this(options, logger, config => new ProducerBuilder<string, byte[]>(config).Build())
    {
    }

    internal KafkaEventPublisher(IOptions<KafkaProducerOptions> options, ILogger<KafkaEventPublisher> logger, Func<ProducerConfig, IProducer<string, byte[]>> factory)
    {
        _options = options;
        _logger = logger;
        _factory = factory;
    }

    public async Task PublishAsync(RawMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var activity = Source.StartActivity($"{KafkaTopics.EventsRaw} publish", ActivityKind.Producer);

        var producer = GetProducer();

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
            await producer.ProduceAsync(
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

    private IProducer<string, byte[]> GetProducer()
    {
        if (_producer is { } existing)
        {
            return existing;
        }

        lock (_gate)
        {
            if (_producer is { } created)
            {
                return created;
            }

            var settings = _options.Value;
            try
            {
                _producer = _factory(new ProducerConfig
                {
                    BootstrapServers = settings.BootstrapServers,
                    ClientId = "crm-api",
                    Acks = Acks.All,
                    EnableIdempotence = true,
                    LingerMs = settings.Producer.LingerMs,
                    MessageTimeoutMs = settings.Producer.MessageTimeoutMs,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kafka producer could not be constructed: {Reason}", ex.Message);
                throw new EventPublishException($"Kafka producer could not be constructed: {ex.Message}", ex);
            }

            return _producer;
        }
    }

    public void Dispose()
    {
        if (_producer is { } producer)
        {
            producer.Flush(TimeSpan.FromSeconds(5));
            producer.Dispose();
        }
    }
}
