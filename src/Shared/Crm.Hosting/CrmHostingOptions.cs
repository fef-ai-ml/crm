using Microsoft.Extensions.Configuration;

namespace Crm.Hosting;

/// <summary>Connection settings shared by every host. Bound from configuration; names match compose environment variables.</summary>
public sealed record CrmHostingOptions(string SqlConnectionString, string RedisConfiguration, string KafkaBootstrapServers)
{
    public static CrmHostingOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new CrmHostingOptions(
            Require(configuration.GetConnectionString("Crm"), "ConnectionStrings:Crm"),
            Require(configuration["Redis:Configuration"], "Redis:Configuration"),
            Require(configuration["Kafka:BootstrapServers"], "Kafka:BootstrapServers"));
    }

    private static string Require(string? value, string key)
        => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{key} is not configured.") : value;
}
