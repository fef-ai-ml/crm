using System.Net;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Ingestion;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class IdentifyTests(CrmStackFixture stack) : IAsyncLifetime
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
    public async Task IT002_identify_creates_a_profile_and_applies_traits()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);

        using var first = await _client.PostWithKeyAsync("/v1/identify", key.Plaintext, new { userId = "u2", traits = new { plan = "pro" } });
        first.StatusCode.Should().Be(HttpStatusCode.Accepted, await first.Content.ReadAsStringAsync());
        var afterFirst = await Eventually.GetAsync(
            () => ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenant.TenantId, "external_id", "u2"),
            "the first identify creates the profile");
        Plan(afterFirst).Should().Be("pro");

        await Task.Delay(TimeSpan.FromMilliseconds(20)); // datetime2(3): make sure the second ReceivedAt is a later millisecond
        using var second = await _client.PostWithKeyAsync("/v1/identify", key.Plaintext, new { userId = "u2", traits = new { plan = "free", city = "SG" } });
        second.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var profile = await Eventually.GetAsync(
            async () => await ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenant.TenantId, "external_id", "u2") is { } p && Plan(p) == "free" ? p : null,
            "the second identify overwrites plan (same Kafka key, so the two are applied in order)");

        using var attributes = JsonDocument.Parse(profile.Attributes);
        attributes.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("plan", "city");
        attributes.RootElement.GetProperty("city").GetString().Should().Be("SG");
        profile.EventCount.Should().Be(0);
        profile.LastSeenAt.Should().BeAfter(afterFirst.LastSeenAt);
        profile.FirstSeenAt.Should().Be(afterFirst.FirstSeenAt);
        (await ProfilesDb.CountProfilesAsync(stack.SqlConnectionString, tenant.TenantId)).Should().Be(1);
        (await ProfilesDb.EventsAsync(stack.SqlConnectionString, tenant.TenantId)).Should().BeEmpty("identify never writes a timeline row (FR-10)");
    }

    private static string? Plan(ProfileRow row)
    {
        using var document = JsonDocument.Parse(row.Attributes);
        return document.RootElement.TryGetProperty("plan", out var plan) ? plan.GetString() : null;
    }
}
