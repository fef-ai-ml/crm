using System.Net;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using Crm.IntegrationTests.Ingestion;
using Crm.Profiles.Application.Queries;
using Crm.Tenancy.Domain;
using Crm.Tenancy.Infrastructure.Auth;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Profiles;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class ProfileReadTests(CrmStackFixture stack) : IAsyncLifetime
{
    private CrmApiFactory _factory = null!;
    private HttpClient _client = null!;
    private SeededTenant _tenant = null!;
    private SeededApiKey _key = null!;
    private Guid _profileId;

    public async Task InitializeAsync()
    {
        _factory = new CrmApiFactory(stack);
        _client = _factory.CreateApiClient();
        _tenant = await TenancySeed.CreateTenantAsync(_factory);
        _key = await TenancySeed.AddApiKeyAsync(_factory, _tenant);

        using var response = await _client.PostWithKeyAsync("/v1/identify", _key.Plaintext,
            new { userId = "u-read", traits = new { email = "Read.Me@X.io", plan = "pro", age = 31 } });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        _profileId = (await Eventually.GetAsync(
            () => ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, _tenant.TenantId, "external_id", "u-read"),
            "profile u-read exists")).Id;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task FR11_detail_returns_core_fields_and_attributes_as_an_object()
    {
        var viewer = TenancySeed.AccessToken(_factory, _tenant.ViewerUserId, _tenant.TenantId, Roles.Viewer);

        var profile = await _client.ReadAsync<ProfileResponse>($"/v1/profiles/{_profileId}", viewer);

        profile.Id.Should().Be(_profileId);
        profile.ExternalId.Should().Be("u-read");
        profile.Email.Should().Be("read.me@x.io");
        profile.Attributes.ValueKind.Should().Be(JsonValueKind.Object);
        profile.Attributes.GetProperty("plan").GetString().Should().Be("pro");
        profile.Attributes.GetProperty("age").GetInt32().Should().Be(31);
        profile.FirstSeenAt.Kind.Should().Be(DateTimeKind.Utc);
        profile.LastSeenAt.Should().BeOnOrAfter(profile.FirstSeenAt);
    }

    [Fact]
    public async Task FR11_detail_json_has_exactly_the_documented_members_and_utc_timestamps()
    {
        var admin = TenancySeed.AccessToken(_factory, _tenant.AdminUserId, _tenant.TenantId, Roles.Admin);
        using var response = await _client.GetAsync($"/v1/profiles/{_profileId}", admin);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        json.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "externalId", "email", "phone", "attributes", "firstSeenAt", "lastSeenAt", "eventCount");
        json.RootElement.GetProperty("lastSeenAt").GetString().Should().EndWith("Z");
    }

    [Fact]
    public async Task IT033_api_key_cannot_read_profiles_and_missing_credentials_are_401()
    {
        using var withKey = new HttpRequestMessage(HttpMethod.Get, "/v1/profiles");
        withKey.Headers.Add(AuthSchemes.ApiKeyHeader, _key.Plaintext);
        using var forbidden = await _client.SendAsync(withKey);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var anonymous = await _client.GetAsync("/v1/profiles");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task FR11_unknown_id_is_404_profile_not_found()
    {
        var viewer = TenancySeed.AccessToken(_factory, _tenant.ViewerUserId, _tenant.TenantId, Roles.Viewer);
        using var response = await _client.GetAsync($"/v1/profiles/{Guid.NewGuid()}", viewer);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("code").GetString().Should().Be("profile.not_found");
    }
}
