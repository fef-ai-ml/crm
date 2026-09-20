using Crm.Contracts;
using Crm.Profiles.Application.Messages;

namespace Crm.Profiles.Infrastructure.Kafka;

internal sealed class DeadLetterPublisher(WorkerProducer producer) : IDeadLetterPublisher
{
    public Task PublishAsync(RawEnvelope envelope, Exception failure, int attempts, CancellationToken cancellationToken)
    {
        var letter = DeadLetterMessageFactory.Create(envelope, failure, attempts);
        return producer.ProduceAsync(KafkaTopics.EventsDlq, letter.Key, letter.Value, letter.Headers, cancellationToken);
    }
}
