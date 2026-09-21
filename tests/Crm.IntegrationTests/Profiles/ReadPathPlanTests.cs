using Crm.IntegrationTests.Fixtures;
using Crm.Profiles.Application.Queries;
using Crm.Tenancy.Domain;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace Crm.IntegrationTests.Profiles;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class ReadPathPlanTests(CrmStackFixture stack) : IAsyncLifetime
{
    private const string SearchIndex = "IX_Profiles_TenantId_LastSeenAt_Id";

    private CrmApiFactory _factory = null!;
    private HttpClient _client = null!;
    private Guid _tenantId;

    public Task InitializeAsync()
    {
        _factory = new CrmApiFactory(stack);
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_tenantId != Guid.Empty)
        {
            await SeedDataset.DeleteAsync(stack.SqlConnectionString, _tenantId);
        }

        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task NFR02_profile_list_pages_are_served_in_index_order_without_a_sort()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        _tenantId = tenant.TenantId;
        await SeedDataset.LoadAsync(_factory, tenant.TenantId);
        var viewer = TenancySeed.AccessToken(_factory, tenant.ViewerUserId, tenant.TenantId, Roles.Viewer);

        using var scope = new AssertionScope();

        var first = await _client.ReadAsync<ProfileListResponse>("/v1/profiles?limit=50", viewer);
        Expect(await QueryPlans.LatestAsync(stack.SqlConnectionString, "profiles-search"), "browse, first page");

        await _client.ReadAsync<ProfileListResponse>($"/v1/profiles?limit=50&cursor={first.NextCursor}", viewer);
        var paged = await QueryPlans.LatestAsync(stack.SqlConnectionString, "profiles-search");
        Expect(paged, "browse, second page");
        paged.PhysicalOperators.Should().Contain("Index Seek", "the cursor predicate must seek into the keyset index (ruling G3)");

        await _client.ReadAsync<ProfileListResponse>("/v1/profiles?q=alice&limit=50", viewer);
        Expect(await QueryPlans.LatestAsync(stack.SqlConnectionString, "profiles-search"), "search q=alice");
    }

    private static void Expect(QueryPlan plan, string what)
    {
        plan.HasSort.Should().BeFalse($"{what}: rows must come out of the index already ordered; operators were {string.Join(" > ", plan.PhysicalOperators)}");
        plan.Indexes.Should().Contain(SearchIndex, $"{what}: the keyset index must serve the query; indexes were {string.Join(", ", plan.Indexes)}");
        plan.Indexes.Should().NotContain("CX_Profiles_TenantId_Id", $"{what}: a covering index needs no lookup into the clustered index");
    }
}
