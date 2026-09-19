namespace Crm.Ingestion.Application.Envelopes;

/// <summary>One record for events.raw, ready to hand to a producer: nothing Kafka-specific leaks into the application layer.</summary>
public sealed record RawMessage(Guid EventId, string MessageType, string Key, byte[] Value, IReadOnlyDictionary<string, string> Headers);
