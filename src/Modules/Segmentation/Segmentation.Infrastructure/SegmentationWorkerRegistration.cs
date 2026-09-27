using Crm.Segmentation.Application.Runs;
using Crm.Segmentation.Infrastructure.Realtime;
using Crm.Segmentation.Infrastructure.Runs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Crm.Segmentation.Infrastructure;

public static class SegmentationWorkerRegistration
{
    /// <summary>The Worker's half of the module: the batch run drain loop and the six-hour schedule (task-7
    /// brief), and the real-time `events.processed` consumer with its segment cache (plan I). The API never calls this — it only needs <see cref="ISegmentRunQueue"/>, registered by
    /// <see cref="SegmentationModule.AddSegmentationModule"/> for POST /segments/{id}/run.</summary>
    public static IServiceCollection AddSegmentationWorker(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // I-4: a misconfigured (zero, negative or unparsable) value here must fall back to the default with a
        // warning, never throw — `SegmentScheduleOptions.Interval` reaching `PeriodicTimer` unguarded, or
        // `CommandTimeoutSeconds` reaching ADO.NET as 0 ("no timeout") or negative (throws per statement), would
        // otherwise crash the whole Worker host (StopHost), taking the Profiles consumers down with it.
        // `SegmentationOptionsDefaults` holds the pure fallback logic; this only logs and applies it.
        services.AddOptions<SegmentRunOptions>()
            .Bind(configuration.GetSection(SegmentRunOptions.SectionName))
            .PostConfigure<ILoggerFactory>((o, loggerFactory) =>
            {
                var logger = loggerFactory.CreateLogger("Crm.Segmentation.Run");
                var effectivePollInterval = SegmentationOptionsDefaults.EffectivePollInterval(o.PollInterval);
                if (effectivePollInterval != o.PollInterval)
                {
                    logger.LogWarning("Segmentation:Run:PollInterval was {Configured}; falling back to {Default}", o.PollInterval, effectivePollInterval);
                    o.PollInterval = effectivePollInterval;
                }

                var effectiveCommandTimeoutSeconds = SegmentationOptionsDefaults.EffectiveCommandTimeoutSeconds(o.CommandTimeoutSeconds);
                if (effectiveCommandTimeoutSeconds != o.CommandTimeoutSeconds)
                {
                    logger.LogWarning("Segmentation:Run:CommandTimeoutSeconds was {Configured}; falling back to {Default}", o.CommandTimeoutSeconds, effectiveCommandTimeoutSeconds);
                    o.CommandTimeoutSeconds = effectiveCommandTimeoutSeconds;
                }
            });

        services.AddOptions<SegmentScheduleOptions>()
            .Bind(configuration.GetSection(SegmentScheduleOptions.SectionName))
            .PostConfigure<ILoggerFactory>((o, loggerFactory) =>
            {
                var logger = loggerFactory.CreateLogger("Crm.Segmentation.Schedule");
                var effectiveInterval = SegmentationOptionsDefaults.EffectiveScheduleInterval(o.Interval);
                if (effectiveInterval != o.Interval)
                {
                    logger.LogWarning("Segmentation:Schedule:Interval was {Configured}; falling back to {Default}", o.Interval, effectiveInterval);
                    o.Interval = effectiveInterval;
                }
            });

        services.AddOptions<RealtimeOptions>()
            .Bind(configuration.GetSection(RealtimeOptions.SectionName))
            .Configure(o => o.BootstrapServers = configuration["Kafka:BootstrapServers"] ?? string.Empty)
            .Validate(o => !string.IsNullOrWhiteSpace(o.BootstrapServers), "Kafka:BootstrapServers is required.")
            .Validate(o => o.Instances is >= 1 and <= 64, "Segmentation:Realtime:Instances must be between 1 and 64.")
            .Validate(o => o.RetryDelaysMs.All(ms => ms >= 0) && o.RestartDelayMs >= 0, "Segmentation:Realtime delays must not be negative.")
            .Validate(o => o.CacheTtl > TimeSpan.Zero, "Segmentation:Realtime:CacheTtl must be positive.")
            .ValidateOnStart();

        services.AddSingleton<SegmentationMetrics>();
        services.AddSingleton<RealtimeSegmentCache>();
        services.AddScoped<RealtimeMessageHandler>();
        services.AddHostedService<SegmentChangeSubscriber>();
        services.AddHostedService<ProcessedEventsConsumerService>();
        services.AddScoped<ISegmentRunner, SegmentRunner>();
        services.AddHostedService<SegmentRunWorker>();
        services.AddHostedService<SegmentScheduler>();
        return services;
    }
}
