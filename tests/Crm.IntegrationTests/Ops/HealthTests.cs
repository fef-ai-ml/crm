using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Ops;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class HealthTests(CrmStackFixture stack) : IAsyncLifetime
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
    public async Task IT041_live_is_200_and_ready_reflects_dependencies()
    {
        var live = await _client.GetAsync(new Uri("/health/live", UriKind.Relative));
        live.StatusCode.Should().Be(HttpStatusCode.OK);

        // The first /health/ready after the host starts can transiently report a dependency as Unhealthy
        // before its check has settled; poll for the steady-state result instead of asserting on the first try.
        var ready = await PollUntilReadyAsync();
        ready.StatusCode.Should().Be(HttpStatusCode.OK, await ready.Content.ReadAsStringAsync());
        var readyBody = JsonDocument.Parse(await ready.Content.ReadAsStringAsync()).RootElement;
        readyBody.GetProperty("checks").GetProperty("sqlserver").GetString().Should().Be("Healthy");
        readyBody.GetProperty("checks").GetProperty("redis").GetString().Should().Be("Healthy");
        readyBody.GetProperty("checks").GetProperty("kafka").GetString().Should().Be("Healthy");

        await stack.PauseRedisAsync();
        try
        {
            var degraded = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
            degraded.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var body = JsonDocument.Parse(await degraded.Content.ReadAsStringAsync()).RootElement;
            body.GetProperty("checks").GetProperty("redis").GetString().Should().Be("Unhealthy");

            var stillLive = await _client.GetAsync(new Uri("/health/live", UriKind.Relative));
            stillLive.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await stack.UnpauseRedisAsync();

            // Poll back to healthy before this test ends, so the next test class in the collection
            // (StackCollectionDefinition serializes them) starts from a healthy stack.
            await PollUntilReadyAsync();
        }
    }

    private async Task<HttpResponseMessage> PollUntilReadyAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var response = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
            if (response.StatusCode == HttpStatusCode.OK || stopwatch.Elapsed >= TimeSpan.FromSeconds(30))
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
    }
}
