using System.Diagnostics;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>The pipeline is asynchronous; assertions poll. The remote engine adds network round trips, so its budget is wider.</summary>
public static class Eventually
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);

    public static TimeSpan DefaultTimeout
        => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TESTCONTAINERS_HOST_OVERRIDE")) ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(60);

    public static async Task<T> GetAsync<T>(Func<Task<T?>> probe, string because, TimeSpan? timeout = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(probe);
        var budget = timeout ?? DefaultTimeout;
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            if (clock.Elapsed > budget)
            {
                throw new TimeoutException($"Not within {budget.TotalSeconds:0} s: {because}");
            }

            await Task.Delay(Poll);
        }
    }

    public static async Task TrueAsync(Func<Task<bool>> probe, string because, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        await GetAsync<object>(async () => await probe() ? new object() : null, because, timeout);
    }
}
