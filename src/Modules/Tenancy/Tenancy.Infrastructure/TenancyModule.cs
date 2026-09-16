using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Tenancy.Infrastructure;

public static class TenancyModule
{
    /// <summary>Registers the Tenancy module. Identity, JWT, and API keys arrive with foundation plan B.</summary>
    public static IServiceCollection AddTenancyModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services;
    }
}
