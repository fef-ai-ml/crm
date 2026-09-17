using System.Diagnostics;
using Crm.Hosting;
using Crm.Ingestion.Infrastructure;
using Crm.Kernel.Data;
using Crm.Kernel.Ids;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Profiles.Infrastructure;
using Crm.Segmentation.Infrastructure;
using Crm.Tenancy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Serilog;

namespace Crm.Api;

/// <summary>Namespaced entry point (not top-level statements) so tests can reference Crm.Api.Program unambiguously.</summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        var (exportPath, hostArgs) = OpenApiExport.Parse(args);
        var builder = WebApplication.CreateBuilder(hostArgs);

        builder.AddCrmSerilog("Crm.Api");
        builder.AddCrmOpenTelemetry("Crm.Api");

        builder.Services.AddCrmHealthChecks(builder.Configuration);
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ctx =>
            ctx.ProblemDetails.Extensions["traceId"] = Activity.Current?.Id ?? ctx.HttpContext.TraceIdentifier);
        builder.Services.AddOpenApi("v1");

        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IIdGenerator, UuidV7IdGenerator>();
        builder.Services.AddScoped<TenantContext>();
        builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        builder.Services.AddScoped<TenantSaveChangesInterceptor>();

        builder.Services
            .AddIngestionModule(builder.Configuration)
            .AddProfilesModule(builder.Configuration)
            .AddSegmentationModule(builder.Configuration)
            .AddTenancyModule(builder.Configuration);

        var app = builder.Build();

        if (exportPath is not null)
        {
            await OpenApiExport.WriteAsync(app, exportPath);
            Log.Information("OpenAPI document written to {Path}", exportPath);
            return;
        }

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseSerilogRequestLogging();

        app.MapOpenApi("/openapi/{documentName}.json");
        if (app.Environment.IsDevelopment())
        {
            app.MapScalarApiReference("/swagger", options => options.WithOpenApiRoutePattern("/openapi/{documentName}.json"));
        }

        app.MapCrmHealth();
        app.MapCrmMetrics();

        var v1 = app.MapGroup("/v1");
        _ = v1; // module endpoint groups (Tenancy, Ingestion, Profiles, Segments) attach here in later plans

        if (app.Environment.IsDevelopment())
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<TenancyDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<ProfilesDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<SegmentationDbContext>().Database.MigrateAsync();
        }

        await app.RunAsync();
    }
}
