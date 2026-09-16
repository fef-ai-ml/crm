using Xunit;
using Crm.Worker;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Crm.Api.UnitTests.Worker;

[Trait("Category", "Unit")]
public class StartupProbeServiceTests
{
    [Fact]
    public async Task Logs_broker_names_once_and_completes()
    {
        var source = Substitute.For<IKafkaMetadataSource>();
        source.GetBrokerNames(Arg.Any<TimeSpan>()).Returns(["kafka:9092"]);
        var logger = Substitute.For<ILogger<StartupProbeService>>();
        var service = new StartupProbeService(source, logger);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        source.Received(1).GetBrokerNames(Arg.Any<TimeSpan>());
        service.LastBrokers.Should().Equal("kafka:9092");
    }

    [Fact]
    public async Task Survives_kafka_failure_without_crashing_the_host()
    {
        var source = Substitute.For<IKafkaMetadataSource>();
        source.GetBrokerNames(Arg.Any<TimeSpan>()).Returns(_ => throw new InvalidOperationException("boom"));
        var service = new StartupProbeService(source, Substitute.For<ILogger<StartupProbeService>>());

        await service.StartAsync(CancellationToken.None);
        var act = async () => await service.ExecuteTask!;

        await act.Should().NotThrowAsync();
        service.LastBrokers.Should().BeEmpty();
    }
}
