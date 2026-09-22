using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using Crm.IntegrationTests.Profiles;
using Crm.Segmentation.Application.Segments;
using Crm.Tenancy.Domain;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;
using Xunit.Abstractions;

namespace Crm.IntegrationTests.Segments;

/// <summary>Task-8 brief, H12: the cached execution plans of the members and changes cursor pages, mirroring
/// Profiles.ReadPathPlanTests. Both assertions here are mutation-checked (report has the evidence) — an
/// unmutated-but-untested plan assertion is exactly what H12 exists to rule out.</summary>
[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class SegmentReadPlanTests(CrmStackFixture stack, ITestOutputHelper testOutput) : IAsyncLifetime
{
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
    public async Task H13_members_cursor_page_seeks_ProfileId_without_a_sort()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        _tenantIds.Add(tenant.TenantId);
        await SeedDataset.LoadAsync(_factory, tenant.TenantId);
        var admin = TenancySeed.AccessToken(_factory, tenant.AdminUserId, tenant.TenantId, Roles.Admin);

        var segmentId = await CreateSegmentAsync(admin, "plan-members", "seed-pro-id.json");
        var runId = await PostRunAsync(segmentId, admin);
        await WaitForFinishedAsync(segmentId, runId, admin);

        // H28: a second segment sharing the same rule was tried here (a finished run whose members fully overlap
        // the one under test), meant to give a wrong access path more same-ProfileId rows to walk through. Diagnosed
        // and dropped — see the fix-round-1 report: on this dataset it had the opposite of the intended effect
        // (with it present, the mutation check below stayed green with the hint removed; without it, the same
        // mutation reddens correctly). A single run is what actually keeps the mutation check honest here.

        var first = await _client.ReadAsync<SegmentMemberListResponse>($"/v1/segments/{segmentId}/members?limit=50", admin);
        await _client.ReadAsync<SegmentMemberListResponse>($"/v1/segments/{segmentId}/members?limit=50&cursor={Uri.EscapeDataString(first.NextCursor!)}", admin);

        // H13/H28: raw SQL cannot carry a TagWith tag — its first line "-- segments-members" is the tag itself.
        // "RunId)))" (bracket-free — parentheses are not a T-SQL LIKE wildcard, only [ and ] are) is the FORCESEEK
        // hint's closing on the first-page statement only: "(PK_SegmentRunMembers (RunId)))" vs the cursor page's
        // "(PK_SegmentRunMembers (RunId, ProfileId)))". "m.ProfileId <" is likewise unique to the cursor page
        // (only appears once @after stops being NULL — there is no @after at all in the first-page statement).
        var firstPage = await QueryPlans.LatestAsync(stack.SqlConnectionString, "segments-members", "RunId)))");
        var cursorPage = await QueryPlans.LatestAsync(stack.SqlConnectionString, "segments-members", "m.ProfileId <");

        using var scope = new AssertionScope();
        if (Found(firstPage, "members, first page"))
        {
            // H28: FORCESEEK pins this to the run's own clustered key — IX_SegmentRunMembers_TenantId_ProfileId
            // is no longer accepted (it is keyed across every run of every segment of the tenant; its RunId
            // residual is not bounded by the pruning retention, it grows with the tenant's segment count).
            firstPage.HasSort.Should().BeFalse($"members, first page: rows must come out of the clustered index already ordered; operators were {string.Join(" > ", firstPage.PhysicalOperators)}; engine: {firstPage.Engine}");
            firstPage.Indexes.Should().BeSubsetOf(["PK_SegmentRunMembers", "CX_Profiles_TenantId_Id"], $"members, first page: indexes were {string.Join(", ", firstPage.Indexes)}");
            firstPage.SeekColumns.Should().Contain("RunId", $"members, first page: FORCESEEK must seek on RunId; seek columns were {string.Join(", ", firstPage.SeekColumns)}; engine: {firstPage.Engine}");
            testOutput.WriteLine($"members, first page: sort={firstPage.HasSort}; indexes={string.Join(", ", firstPage.Indexes)}; seek columns={string.Join(", ", firstPage.SeekColumns)}; operators={string.Join(" > ", firstPage.PhysicalOperators)}");
        }

        if (Found(cursorPage, "members, cursor page"))
        {
            cursorPage.HasSort.Should().BeFalse($"members, cursor page: rows must come out of the clustered index already ordered; operators were {string.Join(" > ", cursorPage.PhysicalOperators)}; engine: {cursorPage.Engine}");
            cursorPage.Indexes.Should().BeSubsetOf(["PK_SegmentRunMembers", "CX_Profiles_TenantId_Id"], $"members, cursor page: indexes were {string.Join(", ", cursorPage.Indexes)}");
            cursorPage.SeekColumns.Should().Contain("RunId", $"members, cursor page: FORCESEEK must seek on RunId; seek columns were {string.Join(", ", cursorPage.SeekColumns)}; engine: {cursorPage.Engine}");
            cursorPage.SeekColumns.Should().Contain("ProfileId", $"members, cursor page: the cursor predicate must seek on ProfileId; seek columns were {string.Join(", ", cursorPage.SeekColumns)}; engine: {cursorPage.Engine}");
            testOutput.WriteLine($"members, cursor page: sort={cursorPage.HasSort}; indexes={string.Join(", ", cursorPage.Indexes)}; seek columns={string.Join(", ", cursorPage.SeekColumns)}; operators={string.Join(" > ", cursorPage.PhysicalOperators)}");
        }
    }

    [Fact]
    public async Task H12_changes_cursor_page_with_since_seeks_OccurredAt_without_a_sort()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        _tenantIds.Add(tenant.TenantId);
        var segmentId = await SegmentsDb.InsertSegmentAsync(stack.SqlConnectionString, tenant.TenantId, "plan-changes", DateTime.UtcNow);
        var admin = TenancySeed.AccessToken(_factory, tenant.AdminUserId, tenant.TenantId, Roles.Admin);

        var baseline = DateTime.UtcNow.AddMinutes(-30);
        for (var i = 0; i < 10; i++)
        {
            await SegmentsDb.InsertMembershipChangeAsync(stack.SqlConnectionString, tenant.TenantId, segmentId, Guid.NewGuid(), baseline.AddSeconds(i), "Entered", "Batch");
        }

        var since = Uri.EscapeDataString(baseline.ToString("O"));
        var first = await _client.ReadAsync<SegmentChangeListResponse>($"/v1/segments/{segmentId}/changes?since={since}&limit=3", admin);
        await _client.ReadAsync<SegmentChangeListResponse>($"/v1/segments/{segmentId}/changes?since={since}&limit=3&cursor={Uri.EscapeDataString(first.NextCursor!)}", admin);

        // "@at" (not "@__at_0"): EF Core 10 names parameters after the C# local used to build the cursor predicate
        // (EfSegmentQueries.ListChangesAsync names it "at"); it appears only once a cursor is present, so this is
        // provably the cursor-page statement, not the first page (which carries "@sinceAt" only).
        var paged = await QueryPlans.LatestAsync(stack.SqlConnectionString, "segments-changes", "@at");

        using var scope = new AssertionScope();
        if (Found(paged, "changes, cursor page with since"))
        {
            paged.HasSort.Should().BeFalse($"changes, cursor page: rows must come out of the clustered index already ordered; operators were {string.Join(" > ", paged.PhysicalOperators)}; engine: {paged.Engine}");
            paged.Indexes.Should().ContainSingle().Which.Should().Be("CX_MembershipChanges_TenantId_SegmentId_OccurredAt_Id");
            paged.SeekColumns.Should().Contain("OccurredAt", $"changes, cursor page: the cursor predicate must seek on OccurredAt; seek columns were {string.Join(", ", paged.SeekColumns)}; engine: {paged.Engine}");
            testOutput.WriteLine($"changes, cursor page: sort={paged.HasSort}; indexes={string.Join(", ", paged.Indexes)}; seek columns={string.Join(", ", paged.SeekColumns)}; operators={string.Join(" > ", paged.PhysicalOperators)}");
        }
    }

    /// <summary>Asserts a plan was found without throwing inside the AssertionScope (I2-B), mirroring ReadPathPlanTests.</summary>
    private static bool Found([NotNullWhen(true)] QueryPlan? plan, string what)
    {
        plan.Should().NotBeNull($"a plan must be cached for a statement of '{what}' — was the endpoint called, and does the query still carry its tag?");
        return plan is not null;
    }

    private async Task<Guid> CreateSegmentAsync(string bearer, string name, string fixtureFileName)
    {
        var definition = RuleFixtures.LoadDefinition(fixtureFileName);
        using var response = await _client.PostAsync("/v1/segments", bearer, new { name, definition });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone().GetProperty("id").GetGuid();
    }

    private async Task<Guid> PostRunAsync(Guid segmentId, string bearer)
    {
        using var response = await _client.PostAsync($"/v1/segments/{segmentId}/run", bearer, new { });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<RunQueuedResponse>();
        return body!.RunId;
    }

    private async Task<SegmentRunListItem> WaitForFinishedAsync(Guid segmentId, Guid runId, string bearer)
        => await Eventually.GetAsync(
            async () =>
            {
                var page = await _client.ReadAsync<SegmentRunListResponse>($"/v1/segments/{segmentId}/runs?limit=200", bearer);
                return page.Items.SingleOrDefault(i => i.RunId == runId) is { Status: "Finished" } run ? run : null;
            },
            $"run {runId} finishes");

}
