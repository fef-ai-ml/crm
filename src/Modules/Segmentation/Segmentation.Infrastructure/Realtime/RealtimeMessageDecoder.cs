using System.Text.Json;
using Crm.Contracts;

namespace Crm.Segmentation.Infrastructure.Realtime;

/// <summary>One record from `events.processed`, as the consumer received it.</summary>
public sealed record ProcessedEnvelope(string Topic, int Partition, long Offset, string? Key, byte[] Value, IReadOnlyDictionary<string, string> Headers);

/// <summary>A decoded `events.processed` message the real-time path acts on (ruling I8).</summary>
internal abstract record RealtimeMessage(Guid TenantId)
{
    public sealed record Tracked(EventTracked Message) : RealtimeMessage(Message.TenantId);

    public sealed record Updated(ProfileUpdated Message) : RealtimeMessage(Message.TenantId);

    public sealed record Merged(ProfileMerged Message) : RealtimeMessage(Message.TenantId);
}

/// <summary>An `events.processed` record that cannot be decoded (I9: logged, counted, skipped).</summary>
internal sealed class RealtimeDecodeException(string message, Exception? inner = null) : Exception(message, inner);

internal static class RealtimeMessageDecoder
{
    /// <summary>Null for a message type the real-time path does not act on.</summary>
    public static RealtimeMessage? Decode(ProcessedEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!envelope.Headers.TryGetValue(KafkaHeaders.MessageType, out var type))
        {
            throw new RealtimeDecodeException($"Header '{KafkaHeaders.MessageType}' is missing.");
        }

        try
        {
            RealtimeMessage? message = type switch
            {
                nameof(EventTracked) => Read<EventTracked>(envelope) is { } m ? new RealtimeMessage.Tracked(m) : null,
                nameof(ProfileUpdated) => Read<ProfileUpdated>(envelope) is { } m ? new RealtimeMessage.Updated(m) : null,
                nameof(ProfileMerged) => Read<ProfileMerged>(envelope) is { } m ? new RealtimeMessage.Merged(m) : null,
                _ => null,
            };

            if (message is null)
            {
                return type is nameof(EventTracked) or nameof(ProfileUpdated) or nameof(ProfileMerged)
                    ? throw new RealtimeDecodeException($"{type} payload is null.")
                    : null;
            }

            return message.TenantId == Guid.Empty ? throw new RealtimeDecodeException($"{type} has no tenantId.") : message;
        }
        catch (JsonException ex)
        {
            throw new RealtimeDecodeException($"{type} payload is not valid JSON: {ex.Message}", ex);
        }
    }

    private static T? Read<T>(ProcessedEnvelope envelope) => JsonSerializer.Deserialize<T>(envelope.Value, ContractJson.Options);
}
