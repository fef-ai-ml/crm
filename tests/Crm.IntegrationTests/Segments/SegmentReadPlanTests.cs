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

        var first = await _client.ReadAsync<SegmentMemberListResponse>($"/v1/segments/{segmentId}/members?limit=50", admin);
        await _client.ReadAsync<SegmentMemberListResponse>($"/v1/segments/{segmentId}/members?limit=50&cursor={Uri.EscapeDataString(first.NextCursor!)}", admin);

        // H13: raw SQL cannot carry a TagWith tag — its first line "-- segments-members" is the tag itself.
        // "m.ProfileId <" is bracket-free and only appears once @after stops being NULL, i.e. the cursor page.
        var paged = await QueryPlans.LatestAsync(stack.SqlConnectionString, "segments-members", "m.ProfileId <");

        using var scope = new AssertionScope();
        if (Found(paged, "members, cursor page"))
        {
            // Measured on SQL Server 2022 (report has the full evidence): the optimizer serves this seek from
            // IX_SegmentRunMembers_TenantId_ProfileId, not the H11-predicted PK_SegmentRunMembers (RunId,
            // ProfileId) — a plan defect in the brief's ruling, not this test: both index shapes let RunId = @run
            // be a cheap residual filter, and IX_SegmentRunMembers_TenantId_ProfileId already orders by ProfileId
            // within the tenant, so it needs no Sort either. Both are accepted; either would be a legitimate H11
            // index to add if the mutation check below ever stops discriminating.
            paged.HasSort.Should().BeFalse($"members, cursor page: rows must come out of an index already ordered by ProfileId; operators were {string.Join(" > ", paged.PhysicalOperators)}; engine: {paged.Engine}");
            paged.Indexes.Should().BeSubsetOf(["PK_SegmentRunMembers", "IX_SegmentRunMembers_TenantId_ProfileId", "CX_Profiles_TenantId_Id"], $"members, cursor page: indexes were {string.Join(", ", paged.Indexes)}");
            paged.SeekColumns.Should().Contain("ProfileId", $"members, cursor page: the cursor predicate must seek on ProfileId; seek columns were {string.Join(", ", paged.SeekColumns)}; engine: {paged.Engine}");
            testOutput.WriteLine($"members, cursor page: sort={paged.HasSort}; indexes={string.Join(", ", paged.Indexes)}; seek columns={string.Join(", ", paged.SeekColumns)}; operators={string.Join(" > ", paged.PhysicalOperators)}");
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
        var definition = LoadDefinition(fixtureFileName);
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

    /// <summary>Mirrors SegmentCrudTests.LoadDefinition: walks up from the test binary to the directory containing Crm.sln.</summary>
    private static JsonElement LoadDefinition(string fixtureFileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Crm.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new DirectoryNotFoundException("Crm.sln not found above " + AppContext.BaseDirectory);
        }

        var path = Path.Combine(dir.FullName, "tests", "fixtures", "rules", "valid", fixtureFileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone().GetProperty("definition");
    }
}
