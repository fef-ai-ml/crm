using Crm.Kernel.Data;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Crm.Kernel.UnitTests.Data;

[Trait("Category", "Unit")]
public class TenantEnforcementTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateTime Now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    public sealed class Widget : Entity
    {
        public string Name { get; set; } = string.Empty;

        public void ForceTenant(Guid tenantId) => TenantId = tenantId;
    }

    public sealed class WidgetDbContext(DbContextOptions<WidgetDbContext> options, ITenantContext tenant)
        : DbContext(options), ITenantScopedDbContext
    {
        public DbSet<Widget> Widgets => Set<Widget>();

        public Guid CurrentTenantId => tenant.TenantId;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>();
            modelBuilder.ApplyTenantFilters(this);
        }
    }

    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime UtcNow => now;
    }

    private static WidgetDbContext CreateContext(TenantContext tenant, string dbName)
    {
        var options = new DbContextOptionsBuilder<WidgetDbContext>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(new TenantSaveChangesInterceptor(tenant, new FixedClock(Now)))
            .Options;
        return new WidgetDbContext(options, tenant);
    }

    private static TenantContext Resolved(Guid tenantId)
    {
        var ctx = new TenantContext();
        ctx.Resolve(tenantId, null, TenantPrincipalKind.User);
        return ctx;
    }

    [Fact]
    public async Task UT054_query_without_resolved_tenant_throws_before_returning_rows()
    {
        var dbName = Guid.NewGuid().ToString();
        await using (var seeded = CreateContext(Resolved(TenantA), dbName))
        {
            seeded.Widgets.Add(new Widget { Name = "a" });
            await seeded.SaveChangesAsync();
        }

        await using var unresolved = CreateContext(new TenantContext(), dbName);
        var act = async () => await unresolved.Widgets.ToListAsync();

        await act.Should().ThrowAsync<TenantContextMissingException>();
    }

    [Fact]
    public async Task UT055_added_entity_is_stamped_with_tenant_and_timestamps()
    {
        await using var ctx = CreateContext(Resolved(TenantA), Guid.NewGuid().ToString());
        var widget = new Widget { Name = "w" };

        ctx.Widgets.Add(widget);
        await ctx.SaveChangesAsync();

        widget.TenantId.Should().Be(TenantA);
        widget.CreatedAt.Should().Be(Now);
        widget.UpdatedAt.Should().Be(Now);
    }

    [Fact]
    public async Task UT055_modified_entity_of_another_tenant_throws()
    {
        var dbName = Guid.NewGuid().ToString();
        await using (var asB = CreateContext(Resolved(TenantB), dbName))
        {
            asB.Widgets.Add(new Widget { Name = "b" });
            await asB.SaveChangesAsync();
        }

        await using var asA = CreateContext(Resolved(TenantA), dbName);
        var foreign = await asA.Widgets.IgnoreQueryFilters().SingleAsync();
        foreign.Name = "hijacked";
        var act = async () => await asA.SaveChangesAsync();

        await act.Should().ThrowAsync<TenantMismatchException>();
    }

    [Fact]
    public async Task UT055_added_entity_pre_stamped_with_another_tenant_throws()
    {
        await using var ctx = CreateContext(Resolved(TenantA), Guid.NewGuid().ToString());
        var widget = new Widget { Name = "x" };
        widget.ForceTenant(TenantB);

        ctx.Widgets.Add(widget);
        var act = async () => await ctx.SaveChangesAsync();

        await act.Should().ThrowAsync<TenantMismatchException>();
    }

    [Fact]
    public async Task Query_filter_hides_other_tenants_rows()
    {
        var dbName = Guid.NewGuid().ToString();
        await using (var asA = CreateContext(Resolved(TenantA), dbName))
        {
            asA.Widgets.Add(new Widget { Name = "a" });
            await asA.SaveChangesAsync();
        }

        await using (var asB = CreateContext(Resolved(TenantB), dbName))
        {
            asB.Widgets.Add(new Widget { Name = "b" });
            await asB.SaveChangesAsync();
        }

        await using var reader = CreateContext(Resolved(TenantA), dbName);
        var names = await reader.Widgets.Select(w => w.Name).ToListAsync();

        names.Should().Equal("a");
    }
}
