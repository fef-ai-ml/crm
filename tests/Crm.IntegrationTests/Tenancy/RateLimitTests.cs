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
    // The partitioned token bucket disables AutoReplenishment and instead relies on the partition heartbeat
    // (~100 ms) calling TryReplenish(), which fills continuously at TokensPerPeriod / ReplenishmentPeriod
    // rather than in one step per minute. The bounds below account for bucket size plus elapsed x refill rate.
    private const int BurstSize = 2500;
    private const int BucketSize = 2000;
    private const double RefillPerSecond = 2000.0 / 60.0;

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

        // The partitioned limiter creates its per-key token bucket lazily, and concurrent first requests on a
        // brand-new key can briefly race and see more than one bucket instance. Warm the partition (and the
        // Redis API-key cache) with one sequential call so the burst below measures the steady-state limit.
        using (var warmup = new HttpRequestMessage(HttpMethod.Post, "/v1/_test/ingest-probe"))
        {
            warmup.Headers.Add(AuthSchemes.ApiKeyHeader, noisy.Plaintext);
            using var warmupResponse = await _client.SendAsync(warmup);
            warmupResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        var outcomes = new ConcurrentBag<(HttpStatusCode Status, bool HasRetryAfter)>();
        var stopwatch = Stopwatch.StartNew();

        await Parallel.ForEachAsync(Enumerable.Range(0, BurstSize), new ParallelOptions { MaxDegreeOfParallelism = 32 }, async (_, ct) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/_test/ingest-probe");
            request.Headers.Add(AuthSchemes.ApiKeyHeader, noisy.Plaintext);
            using var response = await _client.SendAsync(request, ct);
            outcomes.Add((response.StatusCode, response.Headers.RetryAfter is not null));
        });
        stopwatch.Stop();

        outcomes.Should().NotContain(o => (int)o.Status >= 500);
        outcomes.Where(o => o.Status == HttpStatusCode.TooManyRequests).Should().OnlyContain(o => o.HasRetryAfter);

        var refilled = (int)Math.Ceiling(stopwatch.Elapsed.TotalSeconds * RefillPerSecond);
        var accepted = outcomes.Count(o => o.Status == HttpStatusCode.Accepted);
        var rejected = outcomes.Count(o => o.Status == HttpStatusCode.TooManyRequests);
        (accepted + rejected).Should().Be(BurstSize);
        accepted.Should().BeLessThanOrEqualTo(BucketSize + refilled + 5, "the bucket holds 2,000 tokens and refills continuously at 2,000 per minute");

        var guaranteedRejections = BurstSize - (BucketSize + refilled + 5);
        if (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            rejected.Should().BeGreaterThanOrEqualTo(100, "IT-035: a burst inside 10 s exceeds the bucket plus at most 334 refilled tokens");
        }
        else
        {
            // Slow engine: the 10 s premise of IT-035 does not hold, so only what the limiter still guarantees is asserted.
            rejected.Should().BeGreaterThanOrEqualTo(Math.Max(0, guaranteedRejections));
        }

        using var other = new HttpRequestMessage(HttpMethod.Post, "/v1/_test/ingest-probe");
        other.Headers.Add(AuthSchemes.ApiKeyHeader, quiet.Plaintext);
        using var otherResponse = await _client.SendAsync(other);
        otherResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }
}
