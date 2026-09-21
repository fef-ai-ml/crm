using Crm.Segmentation.Application.Runs;
using Crm.Segmentation.Infrastructure.Runs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Segmentation.Infrastructure;

public static class SegmentationWorkerRegistration
{
    /// <summary>The Worker's half of the module: the batch run drain loop and the six-hour schedule (task-7
    /// brief). The API never calls this — it only needs <see cref="ISegmentRunQueue"/>, registered by
    /// <see cref="SegmentationModule.AddSegmentationModule"/> for POST /segments/{id}/run.</summary>
    public static IServiceCollection AddSegmentationWorker(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SegmentRunOptions>().Bind(configuration.GetSection(SegmentRunOptions.SectionName));
        // A misconfigured (zero or negative) poll interval would spin the drain loop or never fire it; fall back
        // to the default instead of either.
        services.PostConfigure<SegmentRunOptions>(o =>
        {
            if (o.PollInterval <= TimeSpan.Zero)
            {
                o.PollInterval = SegmentRunOptions.DefaultPollInterval;
            }
        });
        services.AddOptions<SegmentScheduleOptions>().Bind(configuration.GetSection(SegmentScheduleOptions.SectionName));

        services.AddSingleton<SegmentationMetrics>();
        services.AddScoped<ISegmentRunner, SegmentRunner>();
        services.AddHostedService<SegmentRunWorker>();
        services.AddHostedService<SegmentScheduler>();
        return services;
    }
}
