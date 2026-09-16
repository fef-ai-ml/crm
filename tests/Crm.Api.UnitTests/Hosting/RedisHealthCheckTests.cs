using Crm.Hosting.Health;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace Crm.Api.UnitTests.Hosting;

[Trait("Category", "Unit")]
public class RedisHealthCheckTests
{
    [Fact]
    public async Task Pre_cancelled_token_yields_unhealthy_promptly()
    {
        var database = Substitute.For<IDatabase>();
        // Never completes on its own; a pre-cancelled token must short-circuit via WaitAsync
        // rather than waiting on this task, so the check returns promptly regardless.
        database.PingAsync(Arg.Any<CommandFlags>()).Returns(new TaskCompletionSource<TimeSpan>().Task);
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        var check = new RedisHealthCheck(redis);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await check.CheckHealthAsync(new HealthCheckContext(), cts.Token);

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }
}
