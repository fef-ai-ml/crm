using Crm.IntegrationTests.Fixtures;
using Crm.Kernel.Data;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Segmentation.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Crm.IntegrationTests.Segments;

/// <summary>
/// IT-031's Segmentation part: rows for tenants A and B in Segments, SegmentRuns, SegmentRunMembers and
/// MembershipChanges inserted directly through SegmentsDb; with A in the tenant context, each DbSet returns
/// only A's rows.
/// </summary>
[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class SegmentationQueryFilterTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc);
    private readonly CrmStackFixture _stack;
    private readonly ServiceProvider _provider;
    private readonly Guid _tenantA = Guid.CreateVersion7();
    private readonly Guid _tenantB = Guid.CreateVersion7();
    private readonly Guid _profileA = Guid.CreateVersion7();
    private readonly Guid _profileB = Guid.CreateVersion7();
    private Guid _segmentA;
    private Guid _runA;

    public SegmentationQueryFilterTests(CrmStackFixture stack)
    {
        _stack = stack;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Crm"] = stack.SqlConnectionString })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<TenantSaveChangesInterceptor>();
        services.AddSegmentationModule(configuration);
        _provider = services.BuildServiceProvider(validateScopes: true);
    }

    public async Task InitializeAsync()
    {
        _segmentA = await SegmentsDb.InsertSegmentAsync(_stack.SqlConnectionString, _tenantA, "A segment", T0);
        var segmentB = await SegmentsDb.InsertSegmentAsync(_stack.SqlConnectionString, _tenantB, "B segment", T0);
        _runA = await SegmentsDb.InsertSegmentRunAsync(_stack.SqlConnectionString, _tenantA, _segmentA, T0);
        var runB = await SegmentsDb.InsertSegmentRunAsync(_stack.SqlConnectionString, _tenantB, segmentB, T0);
        await SegmentsDb.InsertSegmentRunMemberAsync(_stack.SqlConnectionString, _tenantA, _runA, _profileA);
        await SegmentsDb.InsertSegmentRunMemberAsync(_stack.SqlConnectionString, _tenantB, runB, _profileB);
        await SegmentsDb.InsertMembershipChangeAsync(_stack.SqlConnectionString, _tenantA, _segmentA, _profileA, T0);
        await SegmentsDb.InsertMembershipChangeAsync(_stack.SqlConnectionString, _tenantB, segmentB, _profileB, T0);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private AsyncServiceScope Scope(Guid tenant)
    {
        var scope = _provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(tenant, null, TenantPrincipalKind.None);
        return scope;
    }

    [Fact]
    public async Task Segments_returns_only_the_current_tenants_row()
    {
        await using var scope = Scope(_tenantA);
        var rows = await scope.ServiceProvider.GetRequiredService<SegmentationDbContext>().Segments.ToListAsync();
        rows.Should().ContainSingle(s => s.Id == _segmentA);
    }

    [Fact]
    public async Task SegmentRuns_returns_only_the_current_tenants_row()
    {
        await using var scope = Scope(_tenantA);
        var rows = await scope.ServiceProvider.GetRequiredService<SegmentationDbContext>().SegmentRuns.ToListAsync();
        rows.Should().ContainSingle(r => r.Id == _runA);
    }

    [Fact]
    public async Task SegmentRunMembers_returns_only_the_current_tenants_row()
    {
        await using var scope = Scope(_tenantA);
        var rows = await scope.ServiceProvider.GetRequiredService<SegmentationDbContext>().SegmentRunMembers.ToListAsync();
        rows.Should().ContainSingle(m => m.RunId == _runA && m.ProfileId == _profileA);
    }

    [Fact]
    public async Task MembershipChanges_returns_only_the_current_tenants_row()
    {
        await using var scope = Scope(_tenantA);
        var rows = await scope.ServiceProvider.GetRequiredService<SegmentationDbContext>().MembershipChanges.ToListAsync();
        rows.Should().ContainSingle(c => c.SegmentId == _segmentA && c.ProfileId == _profileA);
    }
}
