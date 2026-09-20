using System.Diagnostics;
using Confluent.Kafka;
using Crm.Contracts;
using Crm.Profiles.Application.Messages;
using Microsoft.Extensions.Logging;

namespace Crm.Profiles.Infrastructure.Kafka;

internal sealed class ProcessedEventPublisher(WorkerProducer producer, ILogger<ProcessedEventPublisher> logger) : IProcessedPublisher
{
    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(1);

    public async Task PublishAsync(ProcessedMessage message, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KafkaHeaders.TenantId] = message.TenantId.ToString("N"),
            [KafkaHeaders.MessageType] = message.MessageType,
            [KafkaHeaders.ContentType] = KafkaHeaders.JsonContentType,
        };
        if (Activity.Current?.Id is { } traceParent)
        {
            headers[KafkaHeaders.TraceParent] = traceParent;
        }

        while (true)
        {
            try
            {
                await producer.ProduceAsync(KafkaTopics.EventsProcessed, message.Key, message.Value, headers, cancellationToken);
                return;
            }
            catch (KafkaException ex)
            {
                // SQL is committed. Giving up would lose ProfileUpdated/ProfileMerged for good (only EventTracked is re-announced on redelivery).
                logger.LogWarning(ex, "Publishing {MessageType} to {Topic} failed: {Reason}; retrying", message.MessageType, KafkaTopics.EventsProcessed, ex.Error.Reason);
                await Task.Delay(RetryPause, cancellationToken);
            }
        }
    }
}
