using Crm.Kernel;
using Crm.Kernel.Data;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Profiles.Infrastructure;
using Crm.Segmentation.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Crm.ArchTests;

[Trait("Category", "Architecture")]
public class TenantScopingTests
{
    private static ServiceProvider BuildProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Crm"] = "Server=localhost;Database=crm;User Id=sa;Password=x;TrustServerCertificate=True",
                ["Redis:Configuration"] = "localhost:6380",
                ["Kafka:BootstrapServers"] = "localhost:19092",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<TenantSaveChangesInterceptor>();
        services.AddProfilesModule(config);
        services.AddSegmentationModule(config);
        return services.BuildServiceProvider();
    }

    public static TheoryData<Type> DbContexts => new() { typeof(ProfilesDbContext), typeof(SegmentationDbContext) };

    [Theory]
    [MemberData(nameof(DbContexts))]
    public void AT006_every_entity_has_TenantId_and_a_query_filter(Type contextType)
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var context = (DbContext)scope.ServiceProvider.GetRequiredService(contextType);

        foreach (var entityType in context.Model.GetEntityTypes().Where(t => typeof(Entity).IsAssignableFrom(t.ClrType) && t.BaseType is null))
        {
            entityType.FindProperty(nameof(Entity.TenantId)).Should().NotBeNull($"{entityType.ClrType.Name} lacks TenantId");
            entityType.GetDeclaredQueryFilters().Should().NotBeEmpty($"{entityType.ClrType.Name} lacks a tenant query filter");
        }
    }

    [Theory]
    [MemberData(nameof(DbContexts))]
    public void AT006_tenant_interceptor_is_registered_and_schema_is_module_owned(Type contextType)
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var context = (DbContext)scope.ServiceProvider.GetRequiredService(contextType);

        var interceptors = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()!.Interceptors ?? [];
        interceptors.Should().Contain(i => i is TenantSaveChangesInterceptor, $"{contextType.Name} must attach TenantSaveChangesInterceptor");

        var expectedSchema = contextType == typeof(ProfilesDbContext) ? "profiles" : "segments";
        context.Model.GetDefaultSchema().Should().Be(expectedSchema);
    }
}
