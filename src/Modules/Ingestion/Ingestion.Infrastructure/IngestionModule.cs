using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Ingestion.Infrastructure;

public static class IngestionModule
{
    /// <summary>Registers the Ingestion module. Producer and endpoints arrive with the Ingestion plan.</summary>
    public static IServiceCollection AddIngestionModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services;
    }
}
