using System.Net;
using Crm.IntegrationTests.Fixtures;
using Crm.IntegrationTests.Ingestion;
using Crm.Profiles.Application.Queries;
using Crm.Tenancy.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Profiles;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class TimelineTests(CrmStackFixture stack) : IAsyncLifetime
{
    // The 120-event batch (and the poll for the pipeline to apply it) is expensive and produces the same
    // fixture for every test in this class, so it runs once per class rather than once per test method.
    private static readonly SemaphoreSlim InitLock = new(1, 1);
    private static SharedFixture? _shared;

    private CrmApiFactory _factory = null!;
    private HttpClient _client = null!;
    private SeededTenant _tenant = null!;
    private string _viewer = null!;
    private Guid _profileId;
    private DateTime _newest;

    public async Task InitializeAsync()
    {
        _factory = new CrmApiFactory(stack);
        _client = _factory.CreateApiClient();

        await InitLock.WaitAsync();
        SharedFixture fixture;
        try
        {
            fixture = _shared ??= await BuildFixtureAsync(_factory);
        }
        finally
        {
            InitLock.Release();
        }

        _tenant = fixture.Tenant;
        _profileId = fixture.ProfileId;
        _newest = fixture.Newest;
        _viewer = TenancySeed.AccessToken(_factory, _tenant.ViewerUserId, _tenant.TenantId, Roles.Viewer);
    }

    private async Task<SharedFixture> BuildFixtureAsync(CrmApiFactory factory)
    {
        var tenant = await TenancySeed.CreateTenantAsync(factory);
        var key = await TenancySeed.AddApiKeyAsync(factory, tenant);

        var now = DateTime.UtcNow;
        var newest = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc).AddMinutes(-1);
        var events = Enumerable.Range(0, 120).Select(i => new
        {
            type = "track",
            userId = "u-timeline",
            @event = i % 3 == 0 ? "purchase" : "app_open",
            // Ties sit at i=49/50 and i=99/100, straddling the limit=50 page boundaries (I4), not buried
            // mid-page where a missing Id tiebreak would go unnoticed.
            timestamp = newest.AddSeconds(-(i is 50 or 100 ? i - 1 : i)).ToString("O"),
            properties = new { n = i },
        }).ToArray();

        using var client = factory.CreateApiClient();
        using var response = await client.PostWithKeyAsync("/v1/batch", key.Plaintext, new { events });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var profile = await Eventually.GetAsync(
            async () => await ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenant.TenantId, "external_id", "u-timeline") is { EventCount: 120 } p ? p : null,
            "all 120 events are applied");

        return new SharedFixture(tenant, profile.Id, newest);
    }

    private sealed record SharedFixture(SeededTenant Tenant, Guid ProfileId, DateTime Newest);

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IT012_pages_are_50_50_20_newest_first_without_overlap()
    {
        var page1 = await PageAsync("limit=50");
        var page2 = await PageAsync($"limit=50&cursor={page1.NextCursor}");
        var page3 = await PageAsync($"limit=50&cursor={page2.NextCursor}");

        new[] { page1.Items.Count, page2.Items.Count, page3.Items.Count }.Should().Equal(50, 50, 20);
        page1.NextCursor.Should().NotBeNull();
        page2.NextCursor.Should().NotBeNull();
        page3.NextCursor.Should().BeNull();

        var all = page1.Items.Concat(page2.Items).Concat(page3.Items).ToList();
        all.Should().HaveCount(120);
        all.Select(e => e.Id).Should().OnlyHaveUniqueItems();
        all.Select(e => e.OccurredAt).Should().BeInDescendingOrder();
        all[0].OccurredAt.Should().Be(_newest);
        all[0].OccurredAt.Kind.Should().Be(DateTimeKind.Utc);
        all[0].Properties.GetProperty("n").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task IT012_default_limit_is_50()
        => (await PageAsync(string.Empty)).Items.Should().HaveCount(50);

    [Fact]
    public async Task IT012_name_filter_returns_the_40_purchases()
    {
        var purchases = await PageAsync("name=purchase&limit=200");

        purchases.Items.Should().HaveCount(40).And.OnlyContain(e => e.Name == "purchase");
        purchases.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task IT012_from_is_inclusive_and_to_is_exclusive()
    {
        var from = Uri.EscapeDataString(_newest.AddSeconds(-5).ToString("O"));
        var to = Uri.EscapeDataString(_newest.AddSeconds(-2).ToString("O"));

        var window = await PageAsync($"from={from}&to={to}");

        window.Items.Select(e => e.Properties.GetProperty("n").GetInt32()).Should().Equal(3, 4, 5);
    }

    [Theory]
    [InlineData("name=bad%20name!")]
    [InlineData("from=2026-09-21T00:00:00Z&to=2026-09-20T00:00:00Z")]
    [InlineData("limit=500")]
    [InlineData("cursor=garbage")]
    public async Task IT012_bad_arguments_are_400(string query)
    {
        using var response = await _client.GetAsync($"/v1/profiles/{_profileId}/events?{query}", _viewer);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task IT012_timeline_of_an_unknown_profile_is_404()
    {
        using var response = await _client.GetAsync($"/v1/profiles/{Guid.NewGuid()}/events", _viewer);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task IT012_a_profiles_scoped_cursor_is_rejected_by_the_timeline()
    {
        var foreign = new Crm.Kernel.Paging.KeysetCursor(DateTime.UtcNow, Guid.NewGuid()).Encode("profiles");
        using var response = await _client.GetAsync($"/v1/profiles/{_profileId}/events?cursor={foreign}", _viewer);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private Task<TimelineResponse> PageAsync(string query)
        => _client.ReadAsync<TimelineResponse>($"/v1/profiles/{_profileId}/events?{query}", _viewer);
}
