using System.Diagnostics.CodeAnalysis;
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
        // No discriminator: this is the first "profiles-search" statement this test has caused to execute, and
        // parallelizeTestCollections=false means no other test can run one concurrently, so "newest" is unambiguous.
        var browseFirst = await QueryPlans.LatestAsync(stack.SqlConnectionString, "profiles-search");
        if (Found(browseFirst, "browse, first page"))
        {
            Expect(browseFirst, "browse, first page");
        }

        await _client.ReadAsync<ProfileListResponse>($"/v1/profiles?limit=50&cursor={first.NextCursor}", viewer);
        // "@at" only appears in the cursor-page shape (EF Core 10 names the parameter @at, not @__at_0). Matching on
        // the parameter name rather than "<= @at" keeps the discriminator valid whatever the predicate's shape.
        // What SeekColumns guards: a cursor that stops being seekable (evaluated as a residual predicate after a
        // tenant-range seek). What it does not: the conjunctive vs disjunctive form — measured on SQL Server 2022,
        // both compile to a seek ranging on LastSeenAt (the disjunctive one as two ranges under one Index Seek).
        var paged = await QueryPlans.LatestAsync(stack.SqlConnectionString, "profiles-search", "@at");
        if (Found(paged, "browse, second page"))
        {
            Expect(paged, "browse, second page");
            paged.SeekColumns.Should().Contain("LastSeenAt",
                $"the cursor predicate must seek to the cursor position, not filter after a tenant-range seek (ruling G3); seek columns were {string.Join(", ", paged.SeekColumns)}; engine: {paged.Engine}");
        }

        await _client.ReadAsync<ProfileListResponse>("/v1/profiles?q=alice&limit=50", viewer);
        // "LIKE @text" only appears in the search shape (T-SQL LIKE treats [ and ] as a wildcard character class, so
        // a bracketed column name such as "[Email] LIKE" cannot be used as a literal LIKE pattern here).
        var searched = await QueryPlans.LatestAsync(stack.SqlConnectionString, "profiles-search", "LIKE @text");
        if (Found(searched, "search q=alice"))
        {
            Expect(searched, "search q=alice");
        }
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
        // ">= @from" only appears in the windowed shape.
        var oneMonth = await QueryPlans.LatestAsync(stack.SqlConnectionString, "profiles-timeline", ">= @from");
        if (Found(oneMonth, "one-month window"))
        {
            oneMonth.Indexes.Should().Contain(TimelineIndex);
            oneMonth.PhysicalOperators.Should().Contain("Clustered Index Seek");
            oneMonth.HasSort.Should().BeFalse($"one partition, total-order key: no sort expected (ruling G6); operators were {string.Join(" > ", oneMonth.PhysicalOperators)}; engine: {oneMonth.Engine}");
        }

        var page1 = await _client.ReadAsync<TimelineResponse>($"/v1/profiles/{pinned.Id}/events?limit=50", viewer);
        await _client.ReadAsync<TimelineResponse>($"/v1/profiles/{pinned.Id}/events?limit=50&cursor={page1.NextCursor}", viewer);
        // "@at" only appears in the cursor-page shape.
        var unrestricted = await QueryPlans.LatestAsync(stack.SqlConnectionString, "profiles-timeline", "@at");
        if (Found(unrestricted, "unrestricted timeline, second page"))
        {
            unrestricted.Indexes.Should().Contain(TimelineIndex);
            unrestricted.PhysicalOperators.Should().Contain("Clustered Index Seek").And.NotContain("Clustered Index Scan");
            unrestricted.SeekColumns.Should().Contain("OccurredAt",
                $"the cursor predicate must seek to the cursor position, not filter after a profile-range seek (ruling G3); seek columns were {string.Join(", ", unrestricted.SeekColumns)}; engine: {unrestricted.Engine}");
            testOutput.WriteLine($"unrestricted timeline, second page: sort={unrestricted.HasSort}; operators: {string.Join(" > ", unrestricted.PhysicalOperators)}");
        }
    }

    /// <summary>Asserts a plan was found without throwing inside the AssertionScope (I2-B): a miss becomes one more
    /// collected failure instead of discarding every failure the scope already gathered.</summary>
    private static bool Found([NotNullWhen(true)] QueryPlan? plan, string what)
    {
        plan.Should().NotBeNull($"a plan must be cached for a statement of '{what}' — was the endpoint called, and does the query still carry its TagWith tag?");
        return plan is not null;
    }

    private static void Expect(QueryPlan plan, string what)
    {
        plan.HasSort.Should().BeFalse($"{what}: rows must come out of the index already ordered; operators were {string.Join(" > ", plan.PhysicalOperators)}; engine: {plan.Engine}");
        plan.Indexes.Should().Contain(SearchIndex, $"{what}: the keyset index must serve the query; indexes were {string.Join(", ", plan.Indexes)}");
        plan.Indexes.Should().NotContain("CX_Profiles_TenantId_Id", $"{what}: a covering index needs no lookup into the clustered index");
    }
}
