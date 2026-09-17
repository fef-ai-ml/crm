using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Crm.IntegrationTests.Fixtures;
using Crm.Tenancy.Infrastructure.Auth;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Tenancy;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class RateLimitTests(CrmStackFixture stack) : IAsyncLifetime
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
    public async Task IT035_bucket_of_2000_rejects_the_excess_with_retry_after_and_spares_other_keys()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var noisy = await TenancySeed.AddApiKeyAsync(_factory, tenant, "noisy");
        var quiet = await TenancySeed.AddApiKeyAsync(_factory, tenant, "quiet");
        var outcomes = new ConcurrentBag<(HttpStatusCode Status, bool HasRetryAfter)>();
        var stopwatch = Stopwatch.StartNew();

        await Parallel.ForEachAsync(Enumerable.Range(0, 2100), new ParallelOptions { MaxDegreeOfParallelism = 32 }, async (_, ct) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/_test/ingest-probe");
            request.Headers.Add(AuthSchemes.ApiKeyHeader, noisy.Plaintext);
            using var response = await _client.SendAsync(request, ct);
            outcomes.Add((response.StatusCode, response.Headers.RetryAfter is not null));
        });

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60), "the burst must land inside one replenishment period");
        outcomes.Should().NotContain(o => (int)o.Status >= 500);
        outcomes.Count(o => o.Status == HttpStatusCode.TooManyRequests).Should().BeGreaterThanOrEqualTo(100);
        outcomes.Where(o => o.Status == HttpStatusCode.TooManyRequests).Should().OnlyContain(o => o.HasRetryAfter);
        outcomes.Count(o => o.Status == HttpStatusCode.Accepted).Should().BeLessThanOrEqualTo(4000, "one full bucket plus at most one replenishment");

        using var other = new HttpRequestMessage(HttpMethod.Post, "/v1/_test/ingest-probe");
        other.Headers.Add(AuthSchemes.ApiKeyHeader, quiet.Plaintext);
        using var otherResponse = await _client.SendAsync(other);
        otherResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }
}
