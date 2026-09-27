using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using Crm.IntegrationTests.Profiles;
using Crm.Segmentation.Application.Segments;
using Crm.Tenancy.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Segments;

/// <summary>Task-8 brief: GET /segments/{id}/members and GET /segments/{id}/changes. The IT-018 read half lives
/// next to the run it observes (BatchRunTests); the IT-030 members/changes 404 half lives with the rest of IT-030
/// (Tenancy/IsolationTests).</summary>
[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class SegmentReadTests(CrmStackFixture stack) : IAsyncLifetime
{
    private readonly CrmApiFactory _factory = new(stack);
    private readonly List<Guid> _tenantIds = [];
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
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
    public async Task Members_page_all_412_of_the_current_run_with_no_overlap()
    {
        var (_, admin) = await SeededTenantAsync();
        var segmentId = await CreateSegmentAsync(admin, "members-paging", "seed-pro-id.json");
        var runId = await PostRunAsync(segmentId, admin);
        await WaitForFinishedAsync(segmentId, runId, admin);

        var allIds = new List<Guid>();
        var pageCount = 0;
        string? cursor = null;
        do
        {
            var path = cursor is null
                ? $"/v1/segments/{segmentId}/members?limit=50"
                : $"/v1/segments/{segmentId}/members?limit=50&cursor={Uri.EscapeDataString(cursor)}";
            var page = await _client.ReadAsync<SegmentMemberListResponse>(path, admin);
            pageCount++;
            allIds.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        pageCount.Should().Be(9, "412 members at limit 50 is 8 full pages plus one of 12");
        allIds.Should().OnlyHaveUniqueItems("no member may repeat across pages");
        allIds.Should().HaveCount(412);
    }

    [Fact]
    public async Task Members_of_a_segment_with_no_finished_run_is_an_empty_page_not_a_404()
    {
        var (_, admin) = await SeededTenantAsync();
        var segmentId = await CreateSegmentAsync(admin, "members-no-run", "seed-pro-id.json");

        var page = await _client.ReadAsync<SegmentMemberListResponse>($"/v1/segments/{segmentId}/members", admin);

        page.Items.Should().BeEmpty();
        page.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task Members_of_an_unknown_segment_is_404()
    {
        var (_, admin) = await SeededTenantAsync();
        using var response = await _client.GetAsync($"/v1/segments/{Guid.NewGuid()}/members", admin);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Changes_of_an_unknown_segment_is_404()
    {
        var (_, admin) = await SeededTenantAsync();
        using var response = await _client.GetAsync($"/v1/segments/{Guid.NewGuid()}/changes", admin);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Changes_with_an_unparsable_since_is_400_invalid_filter()
    {
        var (tenant, admin) = await SeededTenantAsync();
        var segmentId = await SegmentsDb.InsertSegmentAsync(stack.SqlConnectionString, tenant.TenantId, "changes-bad-since", DateTime.UtcNow);

        using var response = await _client.GetAsync($"/v1/segments/{segmentId}/changes?since=not-a-timestamp", admin);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("segments.invalid_filter");
    }

    /// <summary>Plan I, ruling I15: the two sources are never merged, so a consumer reads one of them. `?source=`
    /// filters exactly (case-insensitively), composes with `since` and the cursor, and anything else is 400.</summary>
    [Fact]
    public async Task I15_changes_filtered_by_source_return_only_that_source()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        _tenantIds.Add(tenant.TenantId);
        var admin = TenancySeed.AccessToken(_factory, tenant.AdminUserId, tenant.TenantId, Roles.Admin);
        var segmentId = await SegmentsDb.InsertSegmentAsync(stack.SqlConnectionString, tenant.TenantId, "changes-by-source", DateTime.UtcNow);

        // The shape plan I's live run found: each profile admitted in real time, then again by the next batch run.
        var at = DateTime.UtcNow.AddMinutes(-5);
        var realtime = new List<Guid>();
        var batch = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var profileId = Guid.NewGuid();
            realtime.Add(await SegmentsDb.InsertMembershipChangeAsync(stack.SqlConnectionString, tenant.TenantId, segmentId, profileId, at.AddSeconds(i), "Entered", "Realtime"));
            batch.Add(await SegmentsDb.InsertMembershipChangeAsync(stack.SqlConnectionString, tenant.TenantId, segmentId, profileId, at.AddMinutes(1), "Entered", "Batch"));
        }

        var page1 = await _client.ReadAsync<SegmentChangeListResponse>($"/v1/segments/{segmentId}/changes?source=Realtime&limit=2", admin);
        var page2 = await _client.ReadAsync<SegmentChangeListResponse>($"/v1/segments/{segmentId}/changes?source=Realtime&limit=2&cursor={Uri.EscapeDataString(page1.NextCursor!)}", admin);
        page2.NextCursor.Should().BeNull();
        page1.Items.Concat(page2.Items).Select(i => i.Id).Should().Equal(realtime.AsEnumerable().Reverse(), "only the Realtime rows, newest first, each once");

        var batchOnly = await _client.ReadAsync<SegmentChangeListResponse>($"/v1/segments/{segmentId}/changes?source=batch", admin);
        batchOnly.Items.Select(i => i.Id).Should().BeEquivalentTo(batch);
        batchOnly.Items.Should().OnlyContain(i => i.Source == "Batch");

        var since = Uri.EscapeDataString(at.AddSeconds(1).ToString("O"));
        var sinceRealtime = await _client.ReadAsync<SegmentChangeListResponse>($"/v1/segments/{segmentId}/changes?source=Realtime&since={since}", admin);
        sinceRealtime.Items.Select(i => i.Id).Should().Equal(realtime[2], realtime[1]);

        var both = await _client.ReadAsync<SegmentChangeListResponse>($"/v1/segments/{segmentId}/changes", admin);
        both.Items.Should().HaveCount(6, "without source both are listed, as before");

        using var invalid = await _client.GetAsync($"/v1/segments/{segmentId}/changes?source=Streaming", admin);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("segments.invalid_filter");
    }

    /// <summary>IT-029, plus a tie fact (task-8 brief): two of the three T2 changes share the same OccurredAt,
    /// straddling the limit=2 page boundary. The conjunctive (OccurredAt, Id) cursor must still return each of
    /// the three exactly once, in a single pass across the two pages, and nothing from T1.</summary>
    [Fact]
    public async Task IT029_changes_since_a_timestamp_pages_only_the_newer_rows_newest_first()
    {
        var (tenant, admin) = await SeededTenantAsync();
        var segmentId = await SegmentsDb.InsertSegmentAsync(stack.SqlConnectionString, tenant.TenantId, "changes-since", DateTime.UtcNow);

        var t1 = DateTime.UtcNow.AddMinutes(-10);
        for (var i = 0; i < 10; i++)
        {
            await SegmentsDb.InsertMembershipChangeAsync(stack.SqlConnectionString, tenant.TenantId, segmentId, Guid.NewGuid(), t1, "Entered", "Batch");
        }

        var t2 = DateTime.UtcNow;
        var changeNewest = await SegmentsDb.InsertMembershipChangeAsync(stack.SqlConnectionString, tenant.TenantId, segmentId, Guid.NewGuid(), t2.AddMilliseconds(2), "Entered", "Realtime");
        var changeTiedA = await SegmentsDb.InsertMembershipChangeAsync(stack.SqlConnectionString, tenant.TenantId, segmentId, Guid.NewGuid(), t2, "Entered", "Realtime");
        var changeTiedB = await SegmentsDb.InsertMembershipChangeAsync(stack.SqlConnectionString, tenant.TenantId, segmentId, Guid.NewGuid(), t2, "Exited", "Realtime");

        var since = Uri.EscapeDataString(t2.AddSeconds(-1).ToString("O"));

        var page1 = await _client.ReadAsync<SegmentChangeListResponse>($"/v1/segments/{segmentId}/changes?since={since}&limit=2", admin);
        page1.Items.Should().HaveCount(2);
        page1.Items.Should().OnlyContain(i => i.Source == "Realtime");
        page1.NextCursor.Should().NotBeNull();

        var page2 = await _client.ReadAsync<SegmentChangeListResponse>($"/v1/segments/{segmentId}/changes?since={since}&limit=2&cursor={Uri.EscapeDataString(page1.NextCursor!)}", admin);
        page2.Items.Should().HaveCount(1);
        page2.NextCursor.Should().BeNull();

        var allItems = page1.Items.Concat(page2.Items).ToList();
        allItems.Select(i => i.Id).Should().BeEquivalentTo([changeNewest, changeTiedA, changeTiedB], "the 3 T2 changes must each appear exactly once, nothing from T1");
        allItems.Select(i => i.OccurredAt).Should().BeInDescendingOrder("newest first, ties broken by Id but never producing a gap or a duplicate");
    }

    private async Task<(SeededTenant Tenant, string Admin)> SeededTenantAsync()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        _tenantIds.Add(tenant.TenantId);
        await SeedDataset.LoadAsync(_factory, tenant.TenantId);
        var admin = TenancySeed.AccessToken(_factory, tenant.AdminUserId, tenant.TenantId, Roles.Admin);
        return (tenant, admin);
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
