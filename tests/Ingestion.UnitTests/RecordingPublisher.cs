using System.Collections.Concurrent;
using Crm.Ingestion.Application;
using Crm.Ingestion.Application.Envelopes;

namespace Crm.Ingestion.UnitTests;

internal sealed class RecordingPublisher : IEventPublisher
{
    public ConcurrentQueue<RawMessage> Published { get; } = new();

    public bool Fail { get; set; }

    public Task PublishAsync(RawMessage message, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new EventPublishException("broker down", new TimeoutException());
        }

        Published.Enqueue(message);
        return Task.CompletedTask;
    }
}
