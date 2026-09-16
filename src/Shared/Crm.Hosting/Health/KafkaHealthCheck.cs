using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Crm.Hosting.Health;

/// <summary>
/// Admin metadata round-trip. Confluent.Kafka exposes only a synchronous GetMetadata, so it runs on the thread pool
/// and the health-check cancellation token (registration timeout) races it via WaitAsync.
/// </summary>
public sealed class KafkaHealthCheck : IHealthCheck, IDisposable
{
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(3);
    private readonly IAdminClient _admin;

    public KafkaHealthCheck(CrmHostingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = options.KafkaBootstrapServers }).Build();
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = await Task.Run(() => _admin.GetMetadata(MetadataTimeout), cancellationToken)
                .WaitAsync(cancellationToken);
            return metadata.Brokers.Count > 0
                ? HealthCheckResult.Healthy($"{metadata.Brokers.Count} broker(s)")
                : HealthCheckResult.Unhealthy("no brokers in metadata");
        }
        catch (OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Kafka metadata request timed out");
        }
        catch (KafkaException ex)
        {
            return HealthCheckResult.Unhealthy("Kafka unreachable", ex);
        }
    }

    public void Dispose() => _admin.Dispose();
}
