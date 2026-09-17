using Crm.Kernel;
using Crm.Kernel.Data;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Profiles.Infrastructure;
using Crm.Segmentation.Infrastructure;
using Crm.Tenancy.Domain;
using Crm.Tenancy.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
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
                ["Auth:SigningKey"] = "arch-test-signing-key-0123456789abcdef0123456789",
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
        services.AddTenancyModule(config);
        return services.BuildServiceProvider();
    }

    public static TheoryData<Type> DbContexts => new() { typeof(ProfilesDbContext), typeof(SegmentationDbContext), typeof(TenancyDbContext) };

    private static string ExpectedSchema(Type contextType) => contextType.Name switch
    {
        nameof(ProfilesDbContext) => "profiles",
        nameof(SegmentationDbContext) => "segments",
        nameof(TenancyDbContext) => "identity",
        _ => throw new ArgumentOutOfRangeException(nameof(contextType), contextType.Name, "unknown context"),
    };

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

        context.Model.GetDefaultSchema().Should().Be(ExpectedSchema(contextType));
    }

    [Fact]
    public void AT006_identity_context_filters_everything_except_tenants_and_identity_tables()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var filtered = context.Model.GetEntityTypes()
            .Where(t => t.GetDeclaredQueryFilters().Count > 0)
            .Select(t => t.ClrType)
            .ToList();
        var unfiltered = context.Model.GetEntityTypes()
            .Where(t => t.GetDeclaredQueryFilters().Count == 0)
            .Select(t => t.ClrType)
            .ToList();

        filtered.Should().BeEquivalentTo([typeof(AppUser), typeof(ApiKey), typeof(RefreshToken)]);
        unfiltered.Should().BeEquivalentTo(
        [
            typeof(Tenant),
            typeof(IdentityRole),
            typeof(IdentityUserRole<string>),
            typeof(IdentityUserClaim<string>),
            typeof(IdentityUserLogin<string>),
            typeof(IdentityUserToken<string>),
            typeof(IdentityRoleClaim<string>),
        ]);
        context.Model.FindEntityType(typeof(AppUser))!.FindProperty(nameof(AppUser.TenantId)).Should().NotBeNull();
    }
}
