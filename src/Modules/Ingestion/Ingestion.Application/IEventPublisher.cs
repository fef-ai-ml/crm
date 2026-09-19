using Crm.Ingestion.Application.Envelopes;

namespace Crm.Ingestion.Application;

public interface IEventPublisher
{
    /// <summary>
    /// Completes only after the broker acknowledged the record (acks=all). Throws <see cref="EventPublishException"/>
    /// when the broker does not acknowledge it; nothing is kept for a later retry. A cancelled call throws
    /// <see cref="OperationCanceledException"/> instead, but the record MAY still have been appended — cancelling
    /// the awaited task does not withdraw the message from librdkafka's send queue, so the delivery cannot be
    /// recalled. Consumers must not assume "exception ⇒ absent" for either exception.
    /// </summary>
    Task PublishAsync(RawMessage message, CancellationToken cancellationToken);
}
