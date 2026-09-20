using System.Globalization;
using Crm.Contracts;
using Crm.Profiles.Application.Messages;

namespace Crm.Profiles.Infrastructure.Kafka;

internal sealed record DeadLetter(string? Key, byte[] Value, IReadOnlyDictionary<string, string> Headers);

/// <summary>FR-07: the original record, untouched, plus why and where it failed.</summary>
internal static class DeadLetterMessageFactory
{
    private const int MaxErrorLength = 1024;
    private const int MaxStackLength = 8192;

    public static DeadLetter Create(RawEnvelope envelope, Exception failure, int attempts)
    {
        var headers = new Dictionary<string, string>(envelope.Headers, StringComparer.Ordinal)
        {
            [KafkaHeaders.DlqError] = Truncate($"{failure.GetType().Name}: {failure.Message}", MaxErrorLength),
            [KafkaHeaders.DlqStack] = Truncate(failure.ToString(), MaxStackLength),
            [KafkaHeaders.DlqAttempt] = attempts.ToString(CultureInfo.InvariantCulture),
            [KafkaHeaders.DlqSourceTopic] = envelope.Topic,
            [KafkaHeaders.DlqSourcePartition] = envelope.Partition.ToString(CultureInfo.InvariantCulture),
            [KafkaHeaders.DlqSourceOffset] = envelope.Offset.ToString(CultureInfo.InvariantCulture),
        };
        return new DeadLetter(envelope.Key, envelope.Value, headers);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
