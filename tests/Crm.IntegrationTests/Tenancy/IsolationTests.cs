using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using Crm.IntegrationTests.Profiles;
using Crm.Profiles.Application.Queries;
using Crm.Tenancy.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Tenancy;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class IsolationTests(CrmStackFixture stack) : IAsyncLifetime
{
    private readonly CrmApiFactory _factory = new(stack);
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IT032_request_without_credentials_is_401_problem_json()
    {
        using var response = await _client.GetAsync("/v1/segments");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("type").GetString().Should().Be("https://crm.example/problems/unauthorized");
        body.RootElement.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        body.RootElement.GetProperty("code").GetString().Should().Be("auth.unauthorized");
    }

    [Fact]
    public async Task IT032_jwt_without_tenant_id_fails_closed_with_500_and_trace_id()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/segments");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TenancySeed.TokenWithoutTenant(_factory, tenant.AdminUserId, Roles.Admin));

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        body.RootElement.GetProperty("type").GetString().Should().Be("https://crm.example/problems/unexpected");
        body.RootElement.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        body.RootElement.GetProperty("code").GetString().Should().Be("server.unexpected");
        text.Should().NotContain("items", "no rows may be returned without a tenant");
    }

    [Fact]
    public async Task IT032_each_tenant_sees_only_its_own_rows()
    {
        var a = await TenancySeed.CreateTenantAsync(_factory);
        var b = await TenancySeed.CreateTenantAsync(_factory);
        await TenancySeed.AddApiKeyAsync(_factory, b);

        (await ReadCountAsync(a)).Should().Be(0);
        (await ReadCountAsync(b)).Should().Be(1);
    }

    [Fact]
    public async Task IT030_another_tenants_profile_reads_are_404()
    {
        var a = await TenancySeed.CreateTenantAsync(_factory);
        var b = await TenancySeed.CreateTenantAsync(_factory);

        // Read-path isolation does not need the pipeline (IT-001 owns that round trip), and going through it here
        // raced a Kafka partition backlog RateLimitTests leaves on one partition right before this class (I1).
        var profileOfBId = await ProfilesDb.InsertProfileAsync(stack.SqlConnectionString, b.TenantId, "u-of-b", DateTime.UtcNow);
        var tokenOfA = TenancySeed.AccessToken(_factory, a.AdminUserId, a.TenantId, Roles.Admin);
        var tokenOfB = TenancySeed.AccessToken(_factory, b.AdminUserId, b.TenantId, Roles.Admin);

        using (var response = await _client.GetAsync($"/v1/profiles/{profileOfBId}", tokenOfA))
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        using (var response = await _client.GetAsync($"/v1/profiles/{profileOfBId}/events", tokenOfA))
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        (await _client.ReadAsync<ProfileListResponse>("/v1/profiles?q=u-of-b", tokenOfA)).Items.Should().BeEmpty();

        using (var response = await _client.GetAsync($"/v1/profiles/{profileOfBId}", tokenOfB))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    /// <summary>IT-030's segment-detail half (the profile half is IT030_another_tenants_profile_reads_are_404 above).</summary>
    [Fact]
    public async Task IT030_another_tenants_segment_is_404()
    {
        var a = await TenancySeed.CreateTenantAsync(_factory);
        var b = await TenancySeed.CreateTenantAsync(_factory);

        var segmentOfBId = await SegmentsDb.InsertSegmentAsync(stack.SqlConnectionString, b.TenantId, "b-only", DateTime.UtcNow);
        var tokenOfA = TenancySeed.AccessToken(_factory, a.AdminUserId, a.TenantId, Roles.Admin);
        var tokenOfB = TenancySeed.AccessToken(_factory, b.AdminUserId, b.TenantId, Roles.Admin);

        using (var response = await _client.GetAsync($"/v1/segments/{segmentOfBId}", tokenOfA))
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("code").GetString().Should().Be("segment.not_found");
        }

        using (var response = await _client.GetAsync($"/v1/segments/{segmentOfBId}", tokenOfB))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    /// <summary>Repointed off the deleted read-probe (which counted the caller's ApiKeys): GET /v1/api-keys is
    /// AdminOnly and already tenant-scoped by the same query filter, so it exercises the identical isolation path.</summary>
    private async Task<int> ReadCountAsync(SeededTenant tenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/api-keys");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TenancySeed.AccessToken(_factory, tenant.AdminUserId, tenant.TenantId, Roles.Admin));
        using var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("items").GetArrayLength();
    }
}
