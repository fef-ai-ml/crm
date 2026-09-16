using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Crm.Hosting.Health;

public sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"ping {latency.TotalMilliseconds:F1} ms");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Redis unreachable", ex);
        }
    }
}
