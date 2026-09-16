using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Crm.Hosting;

public static class HealthCheckExtensions
{
    public const string ReadyTag = "ready";

    /// <summary>Registers the Redis multiplexer (singleton) and the three readiness checks tagged "ready".</summary>
    public static IServiceCollection AddCrmHealthChecks(this IServiceCollection services, IConfiguration configuration)
    {
        var options = CrmHostingOptions.From(configuration);
        services.AddSingleton(options);
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var redisOptions = ConfigurationOptions.Parse(options.RedisConfiguration);
            redisOptions.AbortOnConnectFail = false;
            redisOptions.ConnectTimeout = 3000;
            return ConnectionMultiplexer.Connect(redisOptions);
        });

        services.AddSingleton<Health.KafkaHealthCheck>();

        services.AddHealthChecks()
            .AddCheck("sqlserver", new Health.SqlServerHealthCheck(options.SqlConnectionString), HealthStatus.Unhealthy, [ReadyTag], TimeSpan.FromSeconds(3))
            .AddTypeActivatedCheck<Health.RedisHealthCheck>("redis", HealthStatus.Unhealthy, [ReadyTag], TimeSpan.FromSeconds(3))
            .AddCheck<Health.KafkaHealthCheck>("kafka", HealthStatus.Unhealthy, [ReadyTag], TimeSpan.FromSeconds(5));

        return services;
    }

    /// <summary>/health/live (process up) and /health/ready (all "ready" checks), both anonymous, JSON body.</summary>
    public static IEndpointRouteBuilder MapCrmHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = Health.HealthResponseWriter.WriteAsync,
        }).AllowAnonymous();

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
            ResponseWriter = Health.HealthResponseWriter.WriteAsync,
        }).AllowAnonymous();

        return endpoints;
    }

    public static IEndpointRouteBuilder MapCrmMetrics(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPrometheusScrapingEndpoint("/metrics").AllowAnonymous();
        return endpoints;
    }
}
