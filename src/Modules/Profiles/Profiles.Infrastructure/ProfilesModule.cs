using Crm.Kernel.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Profiles.Infrastructure;

public static class ProfilesModule
{
    public static IServiceCollection AddProfilesModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Crm")
            ?? throw new InvalidOperationException("ConnectionStrings:Crm is not configured.");

        services.AddDbContext<ProfilesDbContext>((sp, options) => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(ProfilesDbContext.MigrationsHistoryTable, ProfilesDbContext.Schema))
            .AddInterceptors(sp.GetRequiredService<TenantSaveChangesInterceptor>()));

        return services;
    }
}
