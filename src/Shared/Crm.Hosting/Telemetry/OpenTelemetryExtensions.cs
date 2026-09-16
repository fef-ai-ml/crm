using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Crm.Hosting;

public static class OpenTelemetryExtensions
{
    /// <summary>
    /// Traces: ASP.NET Core, HttpClient, EF Core. Metrics: ASP.NET Core, HttpClient, runtime, Prometheus scrape endpoint.
    /// OTLP export is enabled only when OTEL_EXPORTER_OTLP_ENDPOINT is set (compose observability profile sets it).
    /// </summary>
    public static IHostApplicationBuilder AddCrmOpenTelemetry(this IHostApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var exportOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceVersion: typeof(OpenTelemetryExtensions).Assembly.GetName().Version?.ToString()))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health") && !ctx.Request.Path.StartsWithSegments("/metrics"))
                    .AddHttpClientInstrumentation()
                    .AddEntityFrameworkCoreInstrumentation()
                    .AddSource("Crm.*");
                if (exportOtlp)
                {
                    tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint!));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("Crm.*")
                    .AddPrometheusExporter();
                if (exportOtlp)
                {
                    metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint!));
                }
            });

        return builder;
    }
}
