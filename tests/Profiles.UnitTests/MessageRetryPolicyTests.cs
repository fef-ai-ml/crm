using Crm.Profiles.Application.Retry;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class MessageRetryPolicyTests
{
    private readonly List<TimeSpan> _waits = [];

    private MessageRetryPolicy Policy() => new(
        [TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2)],
        (wait, _) =>
        {
            _waits.Add(wait);
            return Task.CompletedTask;
        });

    [Fact]
    public async Task Success_on_the_first_attempt_does_not_wait()
    {
        var result = await Policy().ExecuteAsync((attempt, _) => Task.FromResult(attempt), CancellationToken.None);

        result.Should().BeEquivalentTo(new { Value = 1, Attempts = 1, Failure = (Exception?)null });
        _waits.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failure_is_retried_with_the_configured_delays()
    {
        var result = await Policy().ExecuteAsync(
            (attempt, _) => attempt < 3 ? throw new InvalidOperationException("transient") : Task.FromResult("ok"),
            CancellationToken.None);

        result.Value.Should().Be("ok");
        result.Attempts.Should().Be(3);
        _waits.Should().Equal(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task After_three_attempts_the_last_exception_is_reported_not_thrown()
    {
        var result = await Policy().ExecuteAsync<string>((attempt, _) => throw new InvalidOperationException($"boom {attempt}"), CancellationToken.None);

        result.Failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("boom 3");
        result.Attempts.Should().Be(3);
        Policy().MaxAttempts.Should().Be(3);
    }

    [Fact]
    public async Task Shutdown_is_not_a_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => Policy().ExecuteAsync<string>((_, ct) => throw new OperationCanceledException(ct), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
