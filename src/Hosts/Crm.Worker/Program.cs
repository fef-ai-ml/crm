using Crm.Hosting;
using Crm.Kernel.Data;
using Crm.Kernel.Ids;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Profiles.Infrastructure;
using Crm.Segmentation.Infrastructure;
using Serilog;

namespace Crm.Worker;

/// <summary>Namespaced entry point so tests can reference Crm.Worker.Program unambiguously.</summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.AddCrmSerilog("Crm.Worker");
        builder.AddCrmOpenTelemetry("Crm.Worker");
        builder.Services.AddCrmHealthChecks(builder.Configuration);

        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IIdGenerator, UuidV7IdGenerator>();
        builder.Services.AddScoped<TenantContext>();
        builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        builder.Services.AddScoped<TenantSaveChangesInterceptor>();

        builder.Services
            .AddProfilesModule(builder.Configuration)
            .AddProfilesWorker(builder.Configuration)
            .AddSegmentationModule(builder.Configuration);

        builder.Services.AddSingleton<IKafkaMetadataSource>(sp => new AdminClientMetadataSource(sp.GetRequiredService<CrmHostingOptions>().KafkaBootstrapServers));
        builder.Services.AddHostedService<StartupProbeService>();

        var app = builder.Build();

        app.UseSerilogRequestLogging();
        app.MapCrmHealth();
        app.MapCrmMetrics();

        await app.RunAsync();
    }
}
