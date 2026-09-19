using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Ingestion;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class KafkaUnavailableTests(CrmStackFixture stack) : IAsyncLifetime
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
    public async Task IT039_kafka_unavailable_is_503_with_retry_after_and_recovers()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        (await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "warm", @event = "warmup" })).StatusCode.Should().Be(HttpStatusCode.Accepted);

        await stack.PauseKafkaAsync();
        try
        {
            var stopwatch = Stopwatch.StartNew();
            using var response = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "during-outage", @event = "purchase" });
            stopwatch.Stop();

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(5));
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("ingest.unavailable");
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "the request fails at message.timeout.ms instead of hanging");
        }
        finally
        {
            await stack.UnpauseKafkaAsync();
        }

        // Recovery: the same producer instance reconnects by itself; poll until the broker is serving again so the
        // next test class in the collection starts from a healthy stack.
        var recovered = HttpStatusCode.ServiceUnavailable;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(60) && recovered != HttpStatusCode.Accepted)
        {
            using var retry = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "after-outage", @event = "purchase" });
            recovered = retry.StatusCode;
        }

        recovered.Should().Be(HttpStatusCode.Accepted);
    }
}
