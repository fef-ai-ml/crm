using Crm.Profiles.Application.Pipeline;
using Crm.Profiles.Infrastructure.Consumers;
using Crm.Profiles.Infrastructure.Kafka;
using Crm.Profiles.Infrastructure.Maintenance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Profiles.Infrastructure;

public static class ProfilesWorkerRegistration
{
    /// <summary>The Worker's half of the module: everything that consumes or produces Kafka. The API never calls this.</summary>
    public static IServiceCollection AddProfilesWorker(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ProfilesConsumerOptions>()
            .Bind(configuration.GetSection(ProfilesConsumerOptions.SectionName))
            .Configure(o => o.BootstrapServers = configuration["Kafka:BootstrapServers"] ?? string.Empty)
            .Validate(o => !string.IsNullOrWhiteSpace(o.BootstrapServers), "Kafka:BootstrapServers is required.")
            .Validate(o => o.Instances is >= 1 and <= 64, "Profiles:Consumer:Instances must be between 1 and 64.")
            .Validate(o => o.RetryDelaysMs.All(ms => ms >= 0) && o.RestartDelayMs >= 0, "Profiles:Consumer delays must not be negative.")
            .ValidateOnStart();

        services.AddSingleton<ProfilesMetrics>();
        services.AddSingleton<SchemaGate>();
        services.AddSingleton<WorkerProducer>();
        services.AddSingleton<IProcessedPublisher, ProcessedEventPublisher>();
        services.AddSingleton<IDeadLetterPublisher, DeadLetterPublisher>();
        services.AddSingleton<IRawMessageHandler, ScopedMessageHandler>();
        services.AddHostedService<RawEventsConsumerService>();
        services.AddHostedService<EventPartitionMaintenanceService>();
        return services;
    }
}
