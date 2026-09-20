using Crm.Contracts;
using Crm.Profiles.Application.Messages;
using Crm.Profiles.Infrastructure.Kafka;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class DeadLetterMessageFactoryTests
{
    private static readonly RawEnvelope Envelope = new(
        KafkaTopics.EventsRaw, 7, 1234, "tenant:u1", [1, 2, 3],
        new Dictionary<string, string> { [KafkaHeaders.TenantId] = "abc", [KafkaHeaders.MessageType] = "TrackEventRaw" });

    [Fact]
    public void FR07_original_bytes_key_and_headers_are_kept_and_the_failure_is_described()
    {
        var failure = Capture(() => throw new PoisonMessageException("bad json"));

        var letter = DeadLetterMessageFactory.Create(Envelope, failure, attempts: 3);

        letter.Key.Should().Be("tenant:u1");
        letter.Value.Should().Equal(1, 2, 3);
        letter.Headers[KafkaHeaders.TenantId].Should().Be("abc");
        letter.Headers[KafkaHeaders.MessageType].Should().Be("TrackEventRaw");
        letter.Headers[KafkaHeaders.DlqError].Should().Be("PoisonMessageException: bad json");
        letter.Headers[KafkaHeaders.DlqStack].Should().Contain(nameof(DeadLetterMessageFactoryTests));
        letter.Headers[KafkaHeaders.DlqAttempt].Should().Be("3");
        letter.Headers[KafkaHeaders.DlqSourceTopic].Should().Be(KafkaTopics.EventsRaw);
        letter.Headers[KafkaHeaders.DlqSourcePartition].Should().Be("7");
        letter.Headers[KafkaHeaders.DlqSourceOffset].Should().Be("1234");
    }

    [Fact]
    public void Header_values_are_bounded_so_a_huge_stack_cannot_exceed_the_broker_message_limit()
    {
        var failure = new InvalidOperationException(new string('x', 10_000));

        var letter = DeadLetterMessageFactory.Create(Envelope, failure, attempts: 3);

        letter.Headers[KafkaHeaders.DlqError].Length.Should().BeLessThanOrEqualTo(1024);
        letter.Headers[KafkaHeaders.DlqStack].Length.Should().BeLessThanOrEqualTo(8192);
    }

    private static PoisonMessageException Capture(Action action)
    {
        try
        {
            action();
        }
        catch (PoisonMessageException ex)
        {
            return ex;
        }

        throw new InvalidOperationException("the action did not throw");
    }
}
