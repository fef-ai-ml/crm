using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
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
    private Guid? _seededTenantId;

    public Task InitializeAsync()
    {
        _factory = new CrmApiFactory(stack);
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        // M1: a 5,050+-row tenant left in the shared database shrinks the margin on every later pipeline test
        // (bigger indexes, staler statistics) — clean it up rather than document the coupling.
        if (_seededTenantId is { } tenantId)
        {
            await SeedDataset.DeleteAsync(stack.SqlConnectionString, tenantId);
        }

        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task LT005_seed_dataset_loads_once_verifies_its_counts_and_is_readable_through_the_api()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var other = await TenancySeed.CreateTenantAsync(_factory);

        // I2: a tenant that already has a profile (but not the pinned one) must still load — the loader verifies
        // the DELTA of what it added, not the tenant's absolute counts. This profile has no plan and no events,
        // so it is (and stays) a member of seed-no-plan, seed-not-pro and seed-dormant.
        await ProfilesDb.InsertProfileAsync(stack.SqlConnectionString, tenant.TenantId, "u-preexisting", DateTime.UtcNow.AddDays(-20));

        var loaded = await SeedDataset.LoadAsync(_factory, tenant.TenantId);
        _seededTenantId = tenant.TenantId;

        loaded.Status.Should().Be("loaded");
        loaded.Profiles.Should().Be(SeedExpectations.Profiles + 1);
        loaded.MergedProfiles.Should().Be(SeedExpectations.MergedProfiles);
        loaded.Events.Should().Be(SeedExpectations.Events);
        loaded.Segments.Should().Equal(new Dictionary<string, int>(SeedExpectations.Segments, StringComparer.Ordinal)
        {
            ["seed-no-plan"] = SeedExpectations.Segments["seed-no-plan"] + 1,
            ["seed-not-pro"] = SeedExpectations.Segments["seed-not-pro"] + 1,
            ["seed-dormant"] = SeedExpectations.Segments["seed-dormant"] + 1,
        });
        TimeSpan.FromMilliseconds(loaded.ElapsedMilliseconds).Should().BeLessThan(TimeSpan.FromSeconds(90), "make seed has 120 s in total (LT-005)");

        (await ScalarAsync("SELECT COUNT(*) FROM profiles.Profiles WHERE TenantId = @t", tenant.TenantId)).Should().Be(5051);
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

        // I8: ltv is a JSON number, not a numeric string. The pinned profile is in ID (not SG); fall back to the
        // first `alice` search hit whose detail carries one, so the assertion is deterministic either way.
        JsonElement? ltv = detail.Attributes.TryGetProperty("ltv", out var pinnedLtv) ? pinnedLtv : null;
        foreach (var candidate in alices.Items)
        {
            if (ltv is not null)
            {
                break;
            }

            var candidateDetail = await _client.ReadAsync<ProfileResponse>($"/v1/profiles/{candidate.Id}", viewer);
            if (candidateDetail.Attributes.TryGetProperty("ltv", out var value))
            {
                ltv = value;
            }
        }

        ltv.Should().NotBeNull("80% of profiles have an ltv");
        ltv!.Value.ValueKind.Should().Be(JsonValueKind.Number);

        var page1 = await _client.ReadAsync<TimelineResponse>($"/v1/profiles/{pinned.Id}/events", viewer);
        page1.Items.Should().HaveCount(50);
        var page2 = await _client.ReadAsync<TimelineResponse>($"/v1/profiles/{pinned.Id}/events?cursor={page1.NextCursor}", viewer);
        page2.Items.Should().HaveCount(50, "E2E-006 scrolls to a second page of 50");
        page1.Items.Select(e => e.Id).Should().NotIntersectWith(page2.Items.Select(e => e.Id));
    }

    /// <summary>I5: the guard the whole blast-radius argument rests on (`Program.cs`'s `IsDevelopment()` check) had
    /// no test proving the route is unmapped — 404, not 401/403, so an ADMIN bearer is used deliberately.</summary>
    [Fact]
    public async Task IT005_seed_endpoint_is_not_mapped_outside_development()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var admin = TenancySeed.AccessToken(_factory, tenant.AdminUserId, tenant.TenantId, Roles.Admin);

        using var notMapped = await PostAsync(_client, "/v1/dev/seed-dataset", admin);

        notMapped.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task IT005_seed_endpoint_is_not_mapped_in_production()
    {
        await using var prodFactory = new CrmApiFactory(stack) { EnvironmentName = "Production" };
        using var client = prodFactory.CreateApiClient();
        var tenant = await TenancySeed.CreateTenantAsync(prodFactory);
        var admin = TenancySeed.AccessToken(prodFactory, tenant.AdminUserId, tenant.TenantId, Roles.Admin);

        using var notMapped = await PostAsync(client, "/v1/dev/seed-dataset", admin);

        notMapped.StatusCode.Should().Be(HttpStatusCode.NotFound);
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
