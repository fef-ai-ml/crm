using Crm.Profiles.Application.Messages;

namespace Crm.Profiles.Infrastructure.Kafka;

internal interface IDeadLetterPublisher
{
    /// <summary>One attempt; a failure propagates so the consumer is rebuilt and the record redelivered (ruling E9).</summary>
    Task PublishAsync(RawEnvelope envelope, Exception failure, int attempts, CancellationToken cancellationToken);
}
