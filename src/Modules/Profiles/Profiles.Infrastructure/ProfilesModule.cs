using Crm.Kernel.Data;
using Crm.Profiles.Application.Pipeline;
using Crm.Profiles.Application.Queries;
using Crm.Profiles.Infrastructure.Caching;
using Crm.Profiles.Infrastructure.Persistence;
using Crm.Profiles.Infrastructure.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        services.TryAddSingleton<IPipelineHooks, NoPipelineHooks>();
        services.AddSingleton<IIdentityCache, RedisIdentityCache>();
        services.AddScoped<IProfileStore, EfProfileStore>();
        services.AddScoped<ProfilePipeline>();
        services.AddScoped<IProfileQueries, EfProfileQueries>();

        return services;
    }
}
