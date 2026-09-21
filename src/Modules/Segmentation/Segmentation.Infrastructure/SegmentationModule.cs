using Crm.Kernel.Data;
using Crm.Segmentation.Application;
using Crm.Segmentation.Application.Compilation;
using Crm.Segmentation.Application.Runs;
using Crm.Segmentation.Application.Segments;
using Crm.Segmentation.Infrastructure.Preview;
using Crm.Segmentation.Infrastructure.Queries;
using Crm.Segmentation.Infrastructure.Runs;
using Crm.Segmentation.Infrastructure.Segments;
using Crm.Segmentation.Infrastructure.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        services.AddScoped<ISegmentRunQueue, SegmentRunQueue>();

        services.TryAddSingleton<ISegmentationHooks, NoSegmentationHooks>();
        services.AddScoped<RuleCompiler>();
        services.AddScoped<CompiledQueryExecutor>();
        services.AddScoped<PreviewService>();

        return services;
    }
}
