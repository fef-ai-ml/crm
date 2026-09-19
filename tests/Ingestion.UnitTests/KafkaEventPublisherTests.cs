using Confluent.Kafka;
using Crm.Contracts;
using Crm.Ingestion.Application;
using Crm.Ingestion.Application.Envelopes;
using Crm.Ingestion.Infrastructure.Kafka;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Crm.Ingestion.UnitTests;

/// <summary>I3: a producer-construction failure must not be cached — the next call gets a fresh attempt.</summary>
public class KafkaEventPublisherTests
{
    private static IOptions<KafkaProducerOptions> Options() => Microsoft.Extensions.Options.Options.Create(new KafkaProducerOptions
    {
        BootstrapServers = "localhost:9092",
    });

    private static RawMessage Message() => new(
        Guid.NewGuid(),
        nameof(TrackEventRaw),
        "key",
        [1, 2, 3],
        new Dictionary<string, string>());

    [Fact]
    public async Task A_construction_failure_is_wrapped_and_not_cached()
    {
        var callCount = 0;
        var producer = Substitute.For<IProducer<string, byte[]>>();
        producer.ProduceAsync(Arg.Any<string>(), Arg.Any<Message<string, byte[]>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DeliveryResult<string, byte[]>>(null!));

        Func<ProducerConfig, IProducer<string, byte[]>> factory = _ =>
        {
            callCount++;
            if (callCount == 1)
            {
                throw new ArgumentException("bad config");
            }

            return producer;
        };

        using var publisher = new KafkaEventPublisher(Options(), NullLogger<KafkaEventPublisher>.Instance, factory);

        var first = async () => await publisher.PublishAsync(Message(), CancellationToken.None);
        await first.Should().ThrowAsync<EventPublishException>();

        await publisher.PublishAsync(Message(), CancellationToken.None);

        callCount.Should().Be(2, "the first failure must not be cached, so the second call retries construction");
        await producer.Received(1).ProduceAsync(Arg.Any<string>(), Arg.Any<Message<string, byte[]>>(), Arg.Any<CancellationToken>());
    }
}
