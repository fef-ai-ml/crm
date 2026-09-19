using System.Diagnostics;
using System.Net;
using Crm.IntegrationTests.Fixtures;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Ops;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class MetricsTests(CrmStackFixture stack) : IAsyncLifetime
{
    private CrmApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new CrmApiFactory(stack);
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Metrics_endpoint_is_anonymous_prometheus_text_with_http_server_metrics()
    {
        // One served request so the ASP.NET Core meter has a measurement to export.
        (await _client.GetAsync(new Uri("/health/live", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.OK);

        // The duration is recorded when the server finishes the request, which can trail the client
        // seeing the response, and the exporter caches a scrape briefly; poll instead of asserting once.
        var deadline = Stopwatch.StartNew();
        string body;
        HttpResponseMessage response;
        do
        {
            response = await _client.GetAsync(new Uri("/metrics", UriKind.Relative));
            body = await response.Content.ReadAsStringAsync();
            if (body.Contains("http_server_request_duration_seconds", StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
        while (deadline.Elapsed < TimeSpan.FromSeconds(10));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "no credentials were sent: /metrics is anonymous for the Prometheus scraper");
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        body.Should().Contain("http_server_request_duration_seconds", "the Grafana HTTP panels query this metric");
    }
}
