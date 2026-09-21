using Crm.Kernel.Data;
using Crm.Segmentation.Application.Segments;
using Crm.Segmentation.Infrastructure.Queries;
using Crm.Segmentation.Infrastructure.Segments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Segmentation.Infrastructure;

public static class SegmentationModule
{
    public static IServiceCollection AddSegmentationModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Crm")
            ?? throw new InvalidOperationException("ConnectionStrings:Crm is not configured.");

        services.AddDbContext<SegmentationDbContext>((sp, options) => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(SegmentationDbContext.MigrationsHistoryTable, SegmentationDbContext.Schema))
            .AddInterceptors(sp.GetRequiredService<TenantSaveChangesInterceptor>()));

        services.AddScoped<ISegmentQueries, EfSegmentQueries>();
        services.AddScoped<ISegmentService, EfSegmentService>();

        return services;
    }
}
