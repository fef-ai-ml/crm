using Confluent.Kafka;

namespace Crm.Worker;

public interface IKafkaMetadataSource
{
    IReadOnlyList<string> GetBrokerNames(TimeSpan timeout);
}

public sealed class AdminClientMetadataSource(string bootstrapServers) : IKafkaMetadataSource, IDisposable
{
    private readonly IAdminClient _admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();

    public IReadOnlyList<string> GetBrokerNames(TimeSpan timeout)
        => _admin.GetMetadata(timeout).Brokers.Select(b => $"{b.Host}:{b.Port}").ToList();

    public void Dispose() => _admin.Dispose();
}

/// <summary>Logs Kafka broker metadata once at start-up. Consumers replace this in the Profiles plan.</summary>
public sealed class StartupProbeService(IKafkaMetadataSource metadata, ILogger<StartupProbeService> logger) : BackgroundService
{
    public IReadOnlyList<string> LastBrokers { get; private set; } = [];

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            LastBrokers = metadata.GetBrokerNames(TimeSpan.FromSeconds(5));
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Kafka reachable; brokers: {Brokers}", string.Join(", ", LastBrokers));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Kafka metadata probe failed at start-up; readiness will report it");
        }

        return Task.CompletedTask;
    }
}
