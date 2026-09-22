using System.Net;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using Crm.IntegrationTests.Profiles;
using Crm.Segmentation.Application.Segments;
using Crm.Segmentation.Infrastructure.Runs;
using Crm.Tenancy.Domain;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Crm.IntegrationTests.Segments;

/// <summary>IT-021 (task-7 brief, H3): calls <see cref="SegmentScheduler.EnqueueScheduledRunsAsync"/> directly
/// instead of waiting on its six-hour timer.</summary>
[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class ScheduleTests(CrmStackFixture stack) : IAsyncLifetime
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
    public async Task IT021_schedule_runs_active_segments_only()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        _tenantIds.Add(tenant.TenantId);
        await SeedDataset.LoadAsync(_factory, tenant.TenantId);
        var admin = TenancySeed.AccessToken(_factory, tenant.AdminUserId, tenant.TenantId, Roles.Admin);
        var definition = RuleFixtures.LoadDefinition("seed-pro-id.json");

        var draftId = await CreateSegmentAsync(admin, "sched-draft", definition, status: null);
        var activeId = await CreateSegmentAsync(admin, "sched-active", definition, status: "Active");
        var archivedId = await CreateSegmentAsync(admin, "sched-archived", definition, status: null);
        (await _client.DeleteAsync($"/v1/segments/{archivedId}", admin)).Dispose();

        var scheduler = stack.Worker.Services.GetServices<IHostedService>().OfType<SegmentScheduler>().Single();

        // Immediately in succession: the drain loop only polls every 5 s, so this cannot race it into finishing
        // the first scheduled run before the second call re-checks for an outstanding one.
        await scheduler.EnqueueScheduledRunsAsync(CancellationToken.None);
        await scheduler.EnqueueScheduledRunsAsync(CancellationToken.None);

        var activeRuns = await _client.ReadAsync<SegmentRunListResponse>($"/v1/segments/{activeId}/runs", admin);
        activeRuns.Items.Should().HaveCount(1, "exactly one Schedule run for the Active segment, never doubled");
        activeRuns.Items[0].Trigger.Should().Be("Schedule");

        (await _client.ReadAsync<SegmentRunListResponse>($"/v1/segments/{draftId}/runs", admin)).Items.Should().BeEmpty();
        (await _client.ReadAsync<SegmentRunListResponse>($"/v1/segments/{archivedId}/runs", admin)).Items.Should().BeEmpty();
    }

    private async Task<Guid> CreateSegmentAsync(string bearer, string name, JsonElement definition, string? status)
    {
        using var response = await _client.PostAsync("/v1/segments", bearer, new { name, definition, status });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone().GetProperty("id").GetGuid();
    }

}
