using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using Crm.IntegrationTests.Profiles;
using Crm.Profiles.Application.Queries;
using Crm.Tenancy.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Ingestion;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class TrackRoundTripTests(CrmStackFixture stack) : IAsyncLifetime
{
    private CrmApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new CrmApiFactory(stack);
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IT001_tracked_event_appears_in_the_profile_timeline_within_2_seconds()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        var viewer = TenancySeed.AccessToken(_factory, tenant.ViewerUserId, tenant.TenantId, Roles.Viewer);

        // Warm the path (JIT, connection pools, first Kafka produce) so the measured call is a steady-state one.
        using (var warm = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u-warm", @event = "app_open" }))
        {
            warm.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        var sent = Stopwatch.StartNew();
        using var response = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u1", @event = "purchase", properties = new { amount = 49.9 } });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var eventId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eventId").GetGuid();

        TimelineEvent? seen = null;
        while (seen is null && sent.Elapsed < Budget * 5)
        {
            var found = await _client.ReadAsync<ProfileListResponse>("/v1/profiles?q=u1", viewer);
            if (found.Items.SingleOrDefault(i => i.ExternalId == "u1") is { } profile)
            {
                var timeline = await _client.ReadAsync<TimelineResponse>($"/v1/profiles/{profile.Id}/events", viewer);
                seen = timeline.Items.SingleOrDefault(e => e.Id == eventId);
            }

            if (seen is null)
            {
                await Task.Delay(50);
            }
        }

        seen.Should().NotBeNull("the event must reach the timeline");
        sent.Elapsed.Should().BeLessThan(Budget, "NFR-02: ingest-to-timeline within 2 s locally (3x in CI, remote engine)");
        seen!.Name.Should().Be("purchase");
        seen.Properties.GetProperty("amount").GetDouble().Should().Be(49.9);
    }

    /// <summary>2 s locally; 6 s in CI or against a remote engine (docs/test/integration.md: 3x multiplier).</summary>
    private static TimeSpan Budget
        => Environment.GetEnvironmentVariable("CI") == "true" || Environment.GetEnvironmentVariable("TESTCONTAINERS_HOST_OVERRIDE") is { Length: > 0 }
            ? TimeSpan.FromSeconds(6)
            : TimeSpan.FromSeconds(2);
}
