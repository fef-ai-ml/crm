using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Crm.IntegrationTests.Fixtures;
using Crm.IntegrationTests.Ingestion;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Ops;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class ObservabilityTests(CrmStackFixture stack) : IAsyncLifetime
{
    private readonly ConcurrentQueue<Activity> _stopped = new();
    private ActivityListener _listener = null!;
    private CrmApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(_listener);
        _factory = new CrmApiFactory(stack);
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _listener.Dispose();
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IT040_one_trace_spans_http_kafka_produce_worker_consume_and_sql()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);

        using var response = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u-trace", @event = "app_open" });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await Eventually.GetAsync(
            () => ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenant.TenantId, "external_id", "u-trace"),
            "the Worker applied the event");

        var consume = await Eventually.GetAsync(
            () => Task.FromResult(_stopped.FirstOrDefault(a => a.Kind == ActivityKind.Consumer && TagsMention(a, tenant.TenantId))),
            "the Worker's consume span for this tenant has stopped");

        var trace = _stopped.Where(a => a.TraceId == consume.TraceId).ToList();
        trace.Should().Contain(a => a.Kind == ActivityKind.Server, "the API's HTTP span starts the trace");
        trace.Should().Contain(a => a.Kind == ActivityKind.Producer, "the Kafka produce span carries traceparent");
        trace.Should().Contain(a => a.Kind == ActivityKind.Client && a.Tags.Any(t => t.Key.StartsWith("db.", StringComparison.Ordinal)), "the SQL insert runs under the consume span");
    }

    [Fact]
    public async Task IT040_metrics_expose_ingest_counter_and_consumer_lag()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        using (var response = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u-metric", @event = "app_open" }))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        var api = await _client.GetStringAsync("/metrics");
        api.Should().MatchRegex(@"crm_ingest_events_total\{[^}]*\} [1-9]");

        using var worker = stack.Worker.CreateClient();
        await Eventually.TrueAsync(
            async () => (await worker.GetStringAsync("/metrics")).Contains("crm_consumer_lag", StringComparison.Ordinal),
            "the Worker reports crm_consumer_lag after its first poll cycle");
    }

    private static bool TagsMention(Activity activity, Guid tenantId)
        => activity.TagObjects.Any(t => t.Value?.ToString() is { } v
            && (v.Contains(tenantId.ToString("N"), StringComparison.OrdinalIgnoreCase) || v.Contains(tenantId.ToString("D"), StringComparison.OrdinalIgnoreCase)));
}
