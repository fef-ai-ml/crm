using System.Net;
using System.Net.Http.Headers;
using Crm.IntegrationTests.Fixtures;
using Crm.Profiles.Application.Queries;
using Crm.Profiles.Application.Seeding;
using Crm.Tenancy.Domain;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Crm.IntegrationTests.Profiles;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class SeedDatasetTests(CrmStackFixture stack) : IAsyncLifetime
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
    public async Task LT005_seed_dataset_loads_once_verifies_its_counts_and_is_readable_through_the_api()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var other = await TenancySeed.CreateTenantAsync(_factory);

        var loaded = await SeedDataset.LoadAsync(_factory, tenant.TenantId);

        loaded.Status.Should().Be("loaded");
        loaded.Profiles.Should().Be(SeedExpectations.Profiles);
        loaded.MergedProfiles.Should().Be(SeedExpectations.MergedProfiles);
        loaded.Events.Should().Be(SeedExpectations.Events);
        loaded.Segments.Should().Equal(SeedExpectations.Segments);
        TimeSpan.FromMilliseconds(loaded.ElapsedMilliseconds).Should().BeLessThan(TimeSpan.FromSeconds(90), "make seed has 120 s in total (LT-005)");

        (await ScalarAsync("SELECT COUNT(*) FROM profiles.Profiles WHERE TenantId = @t", tenant.TenantId)).Should().Be(5050);
        (await ScalarAsync("SELECT COUNT(*) FROM profiles.Events WHERE TenantId = @t", tenant.TenantId)).Should().Be(100_000);
        (await ScalarAsync("SELECT COUNT(*) FROM profiles.UserIdentities WHERE TenantId = @t AND Kind = 'anonymous_id'", tenant.TenantId)).Should().Be(50);
        (await ScalarAsync("SELECT COUNT(*) FROM profiles.Profiles p WHERE p.TenantId = @t AND p.MergedIntoId IS NULL AND p.EventCount <> (SELECT COUNT(*) FROM profiles.Events e WHERE e.TenantId = p.TenantId AND e.ProfileId = p.Id)", tenant.TenantId))
            .Should().Be(0, "EventCount is COUNT(*) of the profile's events");
        (await ScalarAsync("SELECT COUNT(*) FROM profiles.Profiles WHERE TenantId = @t", other.TenantId)).Should().Be(0);

        var again = await SeedDataset.LoadAsync(_factory, tenant.TenantId);
        again.Status.Should().Be("exists");
        (await ScalarAsync("SELECT COUNT(*) FROM profiles.Events WHERE TenantId = @t", tenant.TenantId)).Should().Be(100_000);

        var viewer = TenancySeed.AccessToken(_factory, tenant.ViewerUserId, tenant.TenantId, Roles.Viewer);
        var alices = await _client.ReadAsync<ProfileListResponse>("/v1/profiles?q=alice", viewer);
        alices.Items.Should().HaveCount(50);
        alices.NextCursor.Should().NotBeNull("100 seeded e-mails start with alice");

        var pinned = (await _client.ReadAsync<ProfileListResponse>("/v1/profiles?q=alice.1%40", viewer)).Items.Should().ContainSingle().Subject;
        pinned.ExternalId.Should().Be(SeedExpectations.PinnedExternalId);
        var detail = await _client.ReadAsync<ProfileResponse>($"/v1/profiles/{pinned.Id}", viewer);
        detail.Attributes.GetProperty("plan").GetString().Should().Be("pro");
        detail.Attributes.GetProperty("country").GetString().Should().Be("ID");

        var page1 = await _client.ReadAsync<TimelineResponse>($"/v1/profiles/{pinned.Id}/events", viewer);
        page1.Items.Should().HaveCount(50);
        var page2 = await _client.ReadAsync<TimelineResponse>($"/v1/profiles/{pinned.Id}/events?cursor={page1.NextCursor}", viewer);
        page2.Items.Should().HaveCount(50, "E2E-006 scrolls to a second page of 50");
        page1.Items.Select(e => e.Id).Should().NotIntersectWith(page2.Items.Select(e => e.Id));
    }

    /// <summary>
    /// The endpoint itself: mapped only in Development (ruling F15). This factory does not load the dataset a
    /// second time through the endpoint — the role check above already covers the loader; this only proves wiring.
    /// </summary>
    [Fact]
    public async Task dev_seed_endpoint_is_AdminOnly_and_requires_authentication()
    {
        await using var devFactory = new CrmApiFactory(stack) { EnvironmentName = "Development" };
        using var client = devFactory.CreateApiClient();
        var tenant = await TenancySeed.CreateTenantAsync(devFactory);

        var viewer = TenancySeed.AccessToken(devFactory, tenant.ViewerUserId, tenant.TenantId, Roles.Viewer);
        using var forbidden = await PostAsync(client, "/v1/dev/seed-dataset", viewer);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var unauthorized = await PostAsync(client, "/v1/dev/seed-dataset", bearer: null);
        unauthorized.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return client.SendAsync(request);
    }

    private async Task<int> ScalarAsync(string sql, Guid tenantId)
    {
        await using var connection = new SqlConnection(stack.SqlConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        command.Parameters.AddWithValue("@t", tenantId);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
