namespace Crm.Profiles.Application.Messages;

/// <summary>A consumed Kafka record without Confluent types, so the application layer stays transport-free.</summary>
public sealed record RawEnvelope(string Topic, int Partition, long Offset, string? Key, byte[] Value, IReadOnlyDictionary<string, string> Headers);
