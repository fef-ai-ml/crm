using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.Contracts;
using Crm.IntegrationTests.Fixtures;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Ingestion;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class IdentifyAndBatchTests(CrmStackFixture stack) : IAsyncLifetime
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
    public async Task IT002_identify_is_accepted_and_lands_on_events_raw_as_IdentifyRaw()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);

        using var response = await _client.PostWithKeyAsync("/v1/identify", key.Plaintext, new { userId = "u2", anonymousId = "a9", traits = new { plan = "pro" } });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var record = (await RawTopicReader.ReadTenantAsync(stack.KafkaBootstrapServers, tenant.TenantId, 1, TimeSpan.FromSeconds(30))).Should().ContainSingle().Subject;
        record.MessageType.Should().Be(nameof(IdentifyRaw));
        record.Key.Should().Be($"{tenant.TenantId:N}:u2");
        var payload = record.Payload.Deserialize<IdentifyRaw>(ContractJson.Options)!;
        payload.AnonymousId.Should().Be("a9");
        payload.Traits.GetProperty("plan").GetString().Should().Be("pro");
    }

    [Fact]
    public async Task IT003_batch_returns_per_item_results_and_publishes_only_the_accepted_items()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        var batch = new
        {
            events = new object[]
            {
                new { type = "track", userId = "u1", @event = "app_open" },
                new { type = "track", userId = "u1", @event = "bad name!" },
                new { type = "identify", userId = "u1", traits = new { plan = "pro" } },
            },
        };

        using var response = await _client.PostWithKeyAsync("/v1/batch", key.Plaintext, batch);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("accepted").GetInt32().Should().Be(2);
        body.GetProperty("rejected").GetInt32().Should().Be(1);
        var results = body.GetProperty("results");
        results[0].GetProperty("eventId").GetGuid().Should().NotBeEmpty();
        results[0].TryGetProperty("errors", out _).Should().BeFalse();
        results[1].TryGetProperty("eventId", out _).Should().BeFalse();
        results[1].GetProperty("errors").GetProperty("events[1].event")[0].GetString().Should().Be("must match [A-Za-z0-9_]{1,64}");

        var records = await RawTopicReader.ReadTenantAsync(stack.KafkaBootstrapServers, tenant.TenantId, 2, TimeSpan.FromSeconds(30));
        records.Select(r => r.MessageType).Should().BeEquivalentTo([nameof(TrackEventRaw), nameof(IdentifyRaw)], "exactly the two accepted items");
    }

    [Fact]
    public async Task A_batch_of_501_items_is_a_400()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        var batch = new { events = Enumerable.Range(0, 501).Select(_ => new { type = "track", userId = "u1", @event = "x" }).ToArray() };

        using var response = await _client.PostWithKeyAsync("/v1/batch", key.Plaintext, batch);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("events")[0].GetString().Should().Be("at most 500 items");
    }
}
