using Crm.Ingestion.Application;
using Crm.Ingestion.Application.Envelopes;
using Crm.Ingestion.Application.Requests;
using Crm.Ingestion.Application.Validation;
using Crm.Ingestion.Infrastructure.Kafka;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Ingestion.Infrastructure;

public static class IngestionModule
{
    /// <summary>Ingest path: validators, envelope factory, service, meter and the events.raw producer. The host supplies IClock and IIdGenerator.</summary>
    public static IServiceCollection AddIngestionModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<KafkaProducerOptions>()
            .Bind(configuration.GetSection(KafkaProducerOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.BootstrapServers), "Kafka:BootstrapServers is required.")
            .Validate(o => o.Producer.MessageTimeoutMs > 0 && o.Producer.LingerMs >= 0, "Kafka:Producer settings must be positive.")
            .ValidateOnStart();
        services.AddSingleton<IValidator<TrackRequest>, TrackRequestValidator>();
        services.AddSingleton<IValidator<IdentifyRequest>, IdentifyRequestValidator>();
        services.AddSingleton<EventEnvelopeFactory>();
        services.AddSingleton<IngestMetrics>();
        services.AddSingleton<IEventPublisher, KafkaEventPublisher>();
        services.AddSingleton<IngestService>();
        return services;
    }
}
