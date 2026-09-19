using Crm.Ingestion.Application.Envelopes;

namespace Crm.Ingestion.Application;

public interface IEventPublisher
{
    /// <summary>Completes only after the broker acknowledged the record (acks=all). Throws <see cref="EventPublishException"/> otherwise; nothing is kept for a later retry.</summary>
    Task PublishAsync(RawMessage message, CancellationToken cancellationToken);
}
