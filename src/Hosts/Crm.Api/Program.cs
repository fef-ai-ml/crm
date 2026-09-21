using Crm.Api.OpenApi;
using Crm.Api.Problems;
using Crm.Hosting;
using Crm.Ingestion.Infrastructure;
using Crm.Ingestion.Infrastructure.Endpoints;
using Crm.Kernel.Data;
using Crm.Kernel.Ids;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Profiles.Infrastructure;
using Crm.Profiles.Infrastructure.Endpoints;
using Crm.Segmentation.Infrastructure;
using Crm.Tenancy.Infrastructure;
using Crm.Tenancy.Infrastructure.Auth;
using Crm.Tenancy.Infrastructure.Endpoints;
using Microsoft.AspNetCore.HttpOverrides;
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
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ProblemDefaults.Apply);
        builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            // Trusted only from networks explicitly listed here (default: none, i.e. loopback only), so the
            // per-client-address rate limiter on /auth (RateLimitPolicies.Auth) cannot be fooled by a
            // caller-supplied X-Forwarded-For unless the proxy hop is a network we actually trust.
            var knownNetworks = builder.Configuration["ForwardedHeaders:KnownNetworks"];
            if (!string.IsNullOrWhiteSpace(knownNetworks))
            {
                foreach (var cidr in knownNetworks.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
                }
            }
        });
        builder.Services.AddOpenApi("v1", options => options
            .AddDocumentTransformer<CrmDocumentTransformer>()
            .AddOperationTransformer<CrmOperationTransformer>());

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

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseForwardedHeaders();
        app.UseSerilogRequestLogging();
        app.UseAuthentication();
        app.UseTenantResolution();
        app.UseRateLimiter();
        app.UseAuthorization();

        app.MapOpenApi("/openapi/{documentName}.json");
        if (app.Environment.IsDevelopment())
        {
            app.MapScalarApiReference("/swagger", options => options.WithOpenApiRoutePattern("/openapi/{documentName}.json"));
        }

        app.MapCrmHealth();
        app.MapCrmMetrics();

        var v1 = app.MapGroup("/v1").AddEndpointFilter<ErrorResultFilter>();
        v1.MapTenancyEndpoints();

        // Composition root: Ingestion may not reference Tenancy, so the policy, limiter and tag are applied here.
        v1.MapGroup(string.Empty)
            .RequireAuthorization(AuthPolicies.IngestOnly)
            .RequireRateLimiting(RateLimitPolicies.Ingest)
            .WithTags(EndpointTags.Ingest)
            .MapIngestionEndpoints();

        // Same composition-root rule as Ingestion: Profiles may not name Tenancy's policies or tags.
        v1.MapGroup(string.Empty)
            .RequireAuthorization(AuthPolicies.AdminOrViewer)
            .WithTags(EndpointTags.Profiles)
            .MapProfilesEndpoints();

        if (app.Environment.IsDevelopment() || exportPath is not null)
        {
            v1.MapTenancyBootstrap(); // in the exported contract regardless of the exporting environment
        }

        if (app.Environment.IsDevelopment())
        {
            v1.MapGroup(string.Empty).RequireAuthorization(AuthPolicies.AdminOnly).MapProfilesDevSeed();
        }

        if (app.Environment.IsEnvironment("Testing"))
        {
            v1.MapTenancyTestProbes();
        }

        if (exportPath is not null)
        {
            await OpenApiExport.WriteAsync(app, exportPath);
            Log.Information("OpenAPI document written to {Path}", exportPath);
            return;
        }

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
