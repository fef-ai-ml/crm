namespace Crm.Profiles.Application.Retry;

public sealed record RetryResult<T>(T? Value, Exception? Failure, int Attempts)
{
    public bool Succeeded => Failure is null;
}

/// <summary>
/// Spec §4.3: a message gets delays.Count + 1 attempts (3 with the default 200 ms and 2 s); after the last one the
/// failure is handed back so the caller can dead-letter it. Shutdown is never counted as a failure.
/// </summary>
public sealed class MessageRetryPolicy(IReadOnlyList<TimeSpan> delays, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public int MaxAttempts => delays.Count + 1;

    public async Task<RetryResult<T>> ExecuteAsync<T>(Func<int, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new RetryResult<T>(await action(attempt, cancellationToken), null, attempt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt >= MaxAttempts)
                {
                    return new RetryResult<T>(default, ex, attempt);
                }

                await _delay(delays[attempt - 1], cancellationToken);
            }
        }
    }
}
