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

        var ready = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
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
        }
    }
}
