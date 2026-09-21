using Crm.IntegrationTests.Fixtures;
using Crm.Profiles.Application.Queries;
using Crm.Tenancy.Domain;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;
using Xunit.Abstractions;

namespace Crm.IntegrationTests.Profiles;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class ReadPathPlanTests(CrmStackFixture stack, ITestOutputHelper testOutput) : IAsyncLifetime
{
    private const string SearchIndex = "IX_Profiles_TenantId_LastSeenAt_Id";
    private const string TimelineIndex = "CX_Events_TenantId_ProfileId_OccurredAt_Id";

    private CrmApiFactory _factory = null!;
    private HttpClient _client = null!;
    private readonly List<Guid> _tenantIds = [];

    public Task InitializeAsync()
    {
        _factory = new CrmApiFactory(stack);
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var tenantId in _tenantIds)
        {
            await SeedDataset.DeleteAsync(stack.SqlConnectionString, tenantId);
        }

        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task NFR02_profile_list_pages_are_served_in_index_order_without_a_sort()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        _tenantIds.Add(tenant.TenantId);
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

    [Fact]
    public async Task NFR02_timeline_pages_seek_the_profile_range_and_a_one_month_window_needs_no_sort()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        _tenantIds.Add(tenant.TenantId);
        await SeedDataset.LoadAsync(_factory, tenant.TenantId);
        var viewer = TenancySeed.AccessToken(_factory, tenant.ViewerUserId, tenant.TenantId, Roles.Viewer);
        var pinned = (await _client.ReadAsync<ProfileListResponse>("/v1/profiles?q=alice.1%40", viewer)).Items.Single();

        // One partition: a window inside the current month (the pinned profile has a mandatory event younger than 3 days;
        // on the 1st–3rd of a month that event may fall into the previous month — then use the previous month).
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        if (now.Day <= 3)
        {
            monthStart = monthStart.AddMonths(-1);
        }

        var from = Uri.EscapeDataString(monthStart.ToString("O"));
        var to = Uri.EscapeDataString(monthStart.AddMonths(1).ToString("O"));

        using var scope = new AssertionScope();

        var window = await _client.ReadAsync<TimelineResponse>($"/v1/profiles/{pinned.Id}/events?from={from}&to={to}&limit=5", viewer);
        window.Items.Should().NotBeEmpty("the window must contain events or the plan says nothing");
        var oneMonth = await QueryPlans.LatestAsync(stack.SqlConnectionString, "profiles-timeline");
        oneMonth.Indexes.Should().Contain(TimelineIndex);
        oneMonth.PhysicalOperators.Should().Contain("Clustered Index Seek");
        oneMonth.HasSort.Should().BeFalse($"one partition, total-order key: no sort expected (ruling G6); operators were {string.Join(" > ", oneMonth.PhysicalOperators)}");

        var page1 = await _client.ReadAsync<TimelineResponse>($"/v1/profiles/{pinned.Id}/events?limit=50", viewer);
        await _client.ReadAsync<TimelineResponse>($"/v1/profiles/{pinned.Id}/events?limit=50&cursor={page1.NextCursor}", viewer);
        var unrestricted = await QueryPlans.LatestAsync(stack.SqlConnectionString, "profiles-timeline");
        unrestricted.Indexes.Should().Contain(TimelineIndex);
        unrestricted.PhysicalOperators.Should().Contain("Clustered Index Seek").And.NotContain("Clustered Index Scan");
        testOutput.WriteLine($"unrestricted timeline, second page: sort={unrestricted.HasSort}; operators: {string.Join(" > ", unrestricted.PhysicalOperators)}");
    }

    private static void Expect(QueryPlan plan, string what)
    {
        plan.HasSort.Should().BeFalse($"{what}: rows must come out of the index already ordered; operators were {string.Join(" > ", plan.PhysicalOperators)}");
        plan.Indexes.Should().Contain(SearchIndex, $"{what}: the keyset index must serve the query; indexes were {string.Join(", ", plan.Indexes)}");
        plan.Indexes.Should().NotContain("CX_Profiles_TenantId_Id", $"{what}: a covering index needs no lookup into the clustered index");
    }
}
