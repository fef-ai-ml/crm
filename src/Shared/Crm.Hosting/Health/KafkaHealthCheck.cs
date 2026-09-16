using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Crm.Hosting.Health;

public sealed class KafkaHealthCheck(string bootstrapServers) : IHealthCheck, IDisposable
{
    private readonly IAdminClient _admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = _admin.GetMetadata(TimeSpan.FromSeconds(3));
            return Task.FromResult(metadata.Brokers.Count > 0
                ? HealthCheckResult.Healthy($"{metadata.Brokers.Count} broker(s)")
                : HealthCheckResult.Unhealthy("no brokers in metadata"));
        }
        catch (KafkaException ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Kafka unreachable", ex));
        }
    }

    public void Dispose() => _admin.Dispose();
}
