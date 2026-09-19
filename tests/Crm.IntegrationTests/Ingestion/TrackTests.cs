using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Crm.Contracts;
using Crm.IntegrationTests.Fixtures;
using Crm.Tenancy.Infrastructure.Auth;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Ingestion;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class TrackTests(CrmStackFixture stack) : IAsyncLifetime
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
    public async Task IT001_track_returns_202_with_a_v7_event_id_and_the_record_is_on_events_raw()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);

        using var response = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u1", @event = "purchase", properties = new { amount = 49.9 } });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var eventId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eventId").GetGuid();
        eventId.Version.Should().Be(7);

        var records = await RawTopicReader.ReadTenantAsync(stack.KafkaBootstrapServers, tenant.TenantId, expected: 1, TimeSpan.FromSeconds(30));
        var record = records.Should().ContainSingle().Subject;
        record.Key.Should().Be($"{tenant.TenantId:N}:u1");
        record.MessageType.Should().Be(nameof(TrackEventRaw));
        record.Headers[KafkaHeaders.ContentType].Should().Be(KafkaHeaders.JsonContentType);
        record.Headers.Should().ContainKey(KafkaHeaders.TraceParent);
        var payload = record.Payload.Deserialize<TrackEventRaw>(ContractJson.Options)!;
        payload.EventId.Should().Be(eventId);
        payload.TenantId.Should().Be(tenant.TenantId);
        payload.Event.Should().Be("purchase");
        payload.Properties.GetProperty("amount").GetDouble().Should().Be(49.9);
        payload.ReceivedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task IT001_p95_latency_of_twenty_calls_is_within_budget()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        (await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "warm", @event = "warmup" })).Dispose();

        var timings = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            using var response = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u1", @event = "purchase" });
            stopwatch.Stop();
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        // NFR-02's 50 ms holds with a local broker. With a remote engine (TESTCONTAINERS_HOST_OVERRIDE set) every
        // acks=all round trip crosses the network, so only a loose ceiling is meaningful there.
        var remote = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TESTCONTAINERS_HOST_OVERRIDE"));
        var p95 = timings.Order().ElementAt(18);
        p95.Should().BeLessThan(remote ? 500 : 50, $"timings: {string.Join(", ", timings.Select(t => t.ToString("F0", CultureInfo.InvariantCulture)))}");
    }

    [Fact]
    public async Task Validation_failures_are_400_problems_with_field_paths_and_publish_nothing()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);

        using var response = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u1", @event = "bad name!" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("ingest.invalid");
        problem.GetProperty("detail").GetString().Should().Be("event must match [A-Za-z0-9_]{1,64}");
        problem.GetProperty("errors").GetProperty("event")[0].GetString().Should().Be("must match [A-Za-z0-9_]{1,64}");
        problem.GetProperty("instance").GetString().Should().Be("/v1/track");

        (await RawTopicReader.ReadTenantAsync(stack.KafkaBootstrapServers, tenant.TenantId, expected: 1, TimeSpan.FromSeconds(5))).Should().BeEmpty();
    }

    [Fact]
    public async Task Malformed_json_is_a_400_problem()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/track") { Content = new StringContent("{\"userId\":", Encoding.UTF8, "application/json") };
        request.Headers.Add(AuthSchemes.ApiKeyHeader, key.Plaintext);

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Ingest_needs_an_api_key_and_refuses_a_dashboard_token()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var body = new { userId = "u1", @event = "purchase" };

        using var anonymous = await _client.PostAsJsonAsync("/v1/track", body);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var withJwt = new HttpRequestMessage(HttpMethod.Post, "/v1/track") { Content = JsonContent.Create(body) };
        withJwt.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TenancySeed.AccessToken(_factory, tenant.AdminUserId, tenant.TenantId, "Admin"));
        using var forbidden = await _client.SendAsync(withJwt);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Metrics_expose_the_series_the_dashboard_queries()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        (await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u1", @event = "purchase" })).Dispose();
        (await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u1", @event = "bad name!" })).Dispose();

        var body = string.Empty;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            body = await _client.GetStringAsync(new Uri("/metrics", UriKind.Relative));
            if (body.Contains("crm_ingest_events_total", StringComparison.Ordinal) && body.Contains("crm_ingest_rejected_total", StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(500);
        }

        body.Should().Contain("crm_ingest_events_total{").And.Contain("type=\"track\"");
        body.Should().Contain("crm_ingest_rejected_total{").And.Contain("reason=\"validation\"");
    }
}
