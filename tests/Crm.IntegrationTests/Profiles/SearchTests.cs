using System.Net;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using Crm.IntegrationTests.Ingestion;
using Crm.Profiles.Application.Queries;
using Crm.Tenancy.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Profiles;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class SearchTests(CrmStackFixture stack) : IAsyncLifetime
{
    private CrmApiFactory _factory = null!;
    private HttpClient _client = null!;
    private SeededTenant _tenant = null!;
    private string _viewer = null!;

    public async Task InitializeAsync()
    {
        _factory = new CrmApiFactory(stack);
        _client = _factory.CreateApiClient();
        _tenant = await TenancySeed.CreateTenantAsync(_factory);
        _viewer = TenancySeed.AccessToken(_factory, _tenant.ViewerUserId, _tenant.TenantId, Roles.Viewer);
        var key = await TenancySeed.AddApiKeyAsync(_factory, _tenant);

        // Three profiles, identified in this order so lastSeenAt ascends: alice, ALI-9, the phone owner.
        await IdentifyAsync(key.Plaintext, "u-alice", new { email = "alice@x.io" });
        await IdentifyAsync(key.Plaintext, "ALI-9", new { plan = "pro" });
        await IdentifyAsync(key.Plaintext, "u-phone", new { phone = "+62 812-3456-789" });
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IT011_q_matches_email_external_id_and_phone_by_prefix_case_insensitively()
    {
        var lower = await SearchAsync("ali");
        lower.Items.Select(i => i.ExternalId).Should().Equal("ALI-9", "u-alice"); // lastSeenAt desc

        var upper = await SearchAsync("ALI");
        upper.Items.Select(i => i.ExternalId).Should().Equal("ALI-9", "u-alice");

        var phone = await SearchAsync(Uri.EscapeDataString("+62812"));
        phone.Items.Should().ContainSingle().Which.Phone.Should().Be("+628123456789");

        var lostPlus = await SearchAsync("+62812"); // '+' decodes to a space
        lostPlus.Items.Should().ContainSingle().Which.ExternalId.Should().Be("u-phone");

        (await SearchAsync("zzz")).Items.Should().BeEmpty();
        (await SearchAsync("zzz")).NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task IT011_blank_q_lists_every_live_profile_newest_first_and_pages_without_overlap()
    {
        var first = await _client.ReadAsync<ProfileListResponse>("/v1/profiles?limit=2", _viewer);
        first.Items.Select(i => i.ExternalId).Should().Equal("u-phone", "ALI-9");
        first.NextCursor.Should().NotBeNull();

        var second = await _client.ReadAsync<ProfileListResponse>($"/v1/profiles?limit=2&cursor={first.NextCursor}", _viewer);
        second.Items.Select(i => i.ExternalId).Should().Equal("u-alice");
        second.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task IT011_like_metacharacters_in_q_match_literally()
        => (await SearchAsync(Uri.EscapeDataString("%"))).Items.Should().BeEmpty();

    [Theory]
    [InlineData("/v1/profiles?limit=0", "pagination.invalid_limit")]
    [InlineData("/v1/profiles?limit=201", "pagination.invalid_limit")]
    [InlineData("/v1/profiles?cursor=garbage", "pagination.invalid_cursor")]
    public async Task IT011_bad_paging_arguments_are_400_problem_json(string path, string code)
    {
        using var response = await _client.GetAsync(path, _viewer);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("code").GetString().Should().Be(code);
    }

    [Fact]
    public async Task IT011_a_timeline_cursor_is_rejected_by_search()
    {
        var foreign = new Crm.Kernel.Paging.KeysetCursor(DateTime.UtcNow, Guid.NewGuid()).Encode("events");
        using var response = await _client.GetAsync($"/v1/profiles?cursor={foreign}", _viewer);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>I4: a missing `Id` tiebreak in ORDER BY would silently drop or duplicate rows across a page
    /// boundary when several profiles share one `lastSeenAt` — exactly what a batch identity import produces.</summary>
    [Fact]
    public async Task IT011_profiles_sharing_one_lastSeenAt_are_paged_exactly_once()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var viewer = TenancySeed.AccessToken(_factory, tenant.ViewerUserId, tenant.TenantId, Roles.Viewer);
        var tiedAt = DateTime.UtcNow;
        var ids = new List<Guid>();
        for (var i = 1; i <= 5; i++)
        {
            ids.Add(await ProfilesDb.InsertProfileAsync(stack.SqlConnectionString, tenant.TenantId, $"tie-{i}", tiedAt));
        }

        var seen = new List<Guid>();
        var pageSizes = new List<int>();
        string? cursor = null;
        do
        {
            var query = cursor is null ? "/v1/profiles?q=tie-&limit=2" : $"/v1/profiles?q=tie-&limit=2&cursor={cursor}";
            var page = await _client.ReadAsync<ProfileListResponse>(query, viewer);
            pageSizes.Add(page.Items.Count);
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        pageSizes.Should().Equal(2, 2, 1);
        seen.Should().BeEquivalentTo(ids);
    }

    private Task<ProfileListResponse> SearchAsync(string encodedQ)
        => _client.ReadAsync<ProfileListResponse>($"/v1/profiles?q={encodedQ}", _viewer);

    private async Task IdentifyAsync(string apiKey, string userId, object traits)
    {
        using var response = await _client.PostWithKeyAsync("/v1/identify", apiKey, new { userId, traits });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await Eventually.GetAsync(
            () => ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, _tenant.TenantId, "external_id", userId),
            $"profile {userId} exists");
        await Task.Delay(20); // lastSeenAt is datetime2(3): keep the three profiles on distinct milliseconds
    }
}
