using Crm.Contracts;
using Crm.Ingestion.Application.Envelopes;
using Crm.Ingestion.Application.Requests;
using Crm.Ingestion.Application.Validation;
using Crm.Kernel;
using FluentValidation;

namespace Crm.Ingestion.Application;

/// <summary>validate → envelope → publish. No database, no cache: the only side effect is the Kafka record.</summary>
public sealed class IngestService(
    IValidator<TrackRequest> trackValidator,
    IValidator<IdentifyRequest> identifyValidator,
    EventEnvelopeFactory envelopes,
    IEventPublisher publisher,
    IngestMetrics metrics)
{
    public async Task<Result<IngestAccepted>> TrackAsync(Guid tenantId, TrackRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Reject(IngestErrors.Invalid("body", "is required"));
        }

        var validation = await trackValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Reject(IngestErrors.Invalid(ValidationFields.From(validation)));
        }

        return await PublishOneAsync(envelopes.Track(tenantId, request), IngestMetrics.TypeTrack, cancellationToken);
    }

    public async Task<Result<IngestAccepted>> IdentifyAsync(Guid tenantId, IdentifyRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Reject(IngestErrors.Invalid("body", "is required"));
        }

        var validation = await identifyValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Reject(IngestErrors.Invalid(ValidationFields.From(validation)));
        }

        return await PublishOneAsync(envelopes.Identify(tenantId, request), IngestMetrics.TypeIdentify, cancellationToken);
    }

    public async Task<Result<BatchAccepted>> BatchAsync(Guid tenantId, BatchRequest? request, CancellationToken cancellationToken)
    {
        var items = request?.Events;
        if (items is null || items.Count == 0)
        {
            metrics.Rejected(IngestMetrics.ReasonValidation);
            return IngestErrors.Invalid("events", "at least 1 item");
        }

        if (items.Count > IngestRules.MaxBatchItems)
        {
            metrics.Rejected(IngestMetrics.ReasonValidation);
            return IngestErrors.Invalid("events", "at most 500 items");
        }

        var results = new BatchItemResult[items.Count];
        var accepted = new List<RawMessage>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var (message, errors) = await BuildItemAsync(tenantId, items[index], index, cancellationToken);
            results[index] = new BatchItemResult(index, message?.EventId, errors);
            if (message is not null)
            {
                accepted.Add(message);
            }
        }

        try
        {
            await Task.WhenAll(accepted.Select(message => publisher.PublishAsync(message, cancellationToken)));
        }
        catch (EventPublishException)
        {
            metrics.Rejected(IngestMetrics.ReasonUnavailable, accepted.Count);
            return IngestErrors.Unavailable;
        }

        Count(accepted, nameof(TrackEventRaw), IngestMetrics.TypeTrack);
        Count(accepted, nameof(IdentifyRaw), IngestMetrics.TypeIdentify);
        var rejected = items.Count - accepted.Count;
        if (rejected > 0)
        {
            metrics.Rejected(IngestMetrics.ReasonValidation, rejected);
        }

        return new BatchAccepted(accepted.Count, rejected, results);
    }

    private async Task<(RawMessage? Message, IReadOnlyDictionary<string, string[]>? Errors)> BuildItemAsync(Guid tenantId, BatchItem? item, int index, CancellationToken cancellationToken)
    {
        var prefix = $"events[{index}]";
        if (item is null)
        {
            return (null, Single(prefix, "must be an object"));
        }

        if (string.Equals(item.Type, "track", StringComparison.OrdinalIgnoreCase))
        {
            var track = item.ToTrack();
            var validation = await trackValidator.ValidateAsync(track, cancellationToken);
            return validation.IsValid ? (envelopes.Track(tenantId, track), null) : (null, ValidationFields.From(validation, prefix + "."));
        }

        if (string.Equals(item.Type, "identify", StringComparison.OrdinalIgnoreCase))
        {
            var identify = item.ToIdentify();
            var validation = await identifyValidator.ValidateAsync(identify, cancellationToken);
            return validation.IsValid ? (envelopes.Identify(tenantId, identify), null) : (null, ValidationFields.From(validation, prefix + "."));
        }

        return (null, Single(prefix + ".type", "must be track or identify"));
    }

    private async Task<Result<IngestAccepted>> PublishOneAsync(RawMessage message, string type, CancellationToken cancellationToken)
    {
        try
        {
            await publisher.PublishAsync(message, cancellationToken);
        }
        catch (EventPublishException)
        {
            metrics.Rejected(IngestMetrics.ReasonUnavailable);
            return IngestErrors.Unavailable;
        }

        metrics.Accepted(type);
        return new IngestAccepted(message.EventId);
    }

    private Error Reject(Error error)
    {
        metrics.Rejected(IngestMetrics.ReasonValidation);
        return error;
    }

    private void Count(List<RawMessage> accepted, string messageType, string metricType)
    {
        var count = accepted.Count(m => m.MessageType == messageType);
        if (count > 0)
        {
            metrics.Accepted(metricType, count);
        }
    }

    private static Dictionary<string, string[]> Single(string path, string message)
        => new(StringComparer.Ordinal) { [path] = [message] };
}
