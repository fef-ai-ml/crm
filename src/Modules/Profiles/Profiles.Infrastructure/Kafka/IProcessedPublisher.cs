using Crm.Profiles.Application.Messages;

namespace Crm.Profiles.Infrastructure.Kafka;

internal interface IProcessedPublisher
{
    /// <summary>Returns once Kafka acknowledged the record. Retries in place until then (ruling E7); only shutdown ends it early.</summary>
    Task PublishAsync(ProcessedMessage message, CancellationToken cancellationToken);
}
