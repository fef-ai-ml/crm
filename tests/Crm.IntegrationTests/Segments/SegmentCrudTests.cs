using System.Net;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using Crm.IntegrationTests.Profiles;
using Crm.Segmentation.Application.Segments;
using Crm.Tenancy.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Segments;

/// <summary>IT-013 and the extra facts task 5's brief lists alongside it: canonical form, H2's always-null
/// realtimeMemberCount, the eligibility classifier wired through, H14's default/status filter, and a tied
/// page boundary. IT-030's segment-detail half lives in Tenancy/IsolationTests.cs (foreign-tenant 404).</summary>
[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class SegmentCrudTests(CrmStackFixture stack) : IAsyncLifetime
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
    public async Task IT013_create_fetch_update_archive_and_duplicate_name_conflict()
    {
        var admin = await AdminTokenAsync();
        var definition = RuleFixtures.LoadDefinition("seed-purchasers.json");

        using var created = await _client.PostAsync("/v1/segments", admin, new { name = "buyers", description = "recent buyers", definition });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        created.Headers.Location.Should().NotBeNull();
        var createdBody = await ReadBodyAsync(created);
        createdBody.GetProperty("status").GetString().Should().Be("Draft");
        var id = createdBody.GetProperty("id").GetGuid();

        using (var fetched = await _client.GetAsync($"/v1/segments/{id}", admin))
        {
            fetched.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var updated = await _client.PutAsync($"/v1/segments/{id}", admin, new { name = "buyers", description = "updated", definition, status = "Active" });
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadBodyAsync(updated)).GetProperty("status").GetString().Should().Be("Active");

        using var archived = await _client.DeleteAsync($"/v1/segments/{id}", admin);
        archived.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var afterArchive = await _client.GetAsync($"/v1/segments/{id}", admin);
        (await ReadBodyAsync(afterArchive)).GetProperty("status").GetString().Should().Be("Archived");

        using var archivedAgain = await _client.DeleteAsync($"/v1/segments/{id}", admin);
        archivedAgain.StatusCode.Should().Be(HttpStatusCode.NoContent, "archiving an already-archived segment is idempotent");

        using var unknownDelete = await _client.DeleteAsync($"/v1/segments/{Guid.NewGuid()}", admin);
        unknownDelete.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var duplicate = await _client.PostAsync("/v1/segments", admin, new { name = "buyers", definition });
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict, "the name is taken even though the first segment is now archived (H15)");
        using var duplicateBody = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
        duplicateBody.RootElement.GetProperty("type").GetString().Should().EndWith("/problems/conflict");
        duplicateBody.RootElement.GetProperty("code").GetString().Should().Be("segment.name_taken");
    }

    [Fact]
    public async Task Invalid_definition_reports_the_rule_parsers_message_verbatim()
    {
        var admin = await AdminTokenAsync();
        // UT-019: "30" (no unit) fails the window pattern.
        var badDefinition = JsonSerializer.SerializeToElement(new { type = "event", name = "purchase", cmp = "eq", count = 1, window = "30" });

        using var response = await _client.PostAsync("/v1/segments", admin, new { name = "bad-window", definition = badDefinition });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("segments.invalid_definition");
        body.RootElement.GetProperty("errors").GetProperty("definition.window").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("window must match ^\\d+(d|h)$ and be at most 365 days");
    }

    [Fact]
    public async Task Missing_definition_is_a_400_not_a_500()
    {
        var admin = await AdminTokenAsync();

        using var response = await _client.PostAsync("/v1/segments", admin, new { name = "no-definition" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("segments.invalid_definition");
        body.RootElement.GetProperty("errors").TryGetProperty("definition", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Field_errors_are_reported_before_definition_errors()
    {
        var admin = await AdminTokenAsync();
        var badDefinition = JsonSerializer.SerializeToElement(new { type = "event", name = "purchase", cmp = "eq", count = 1, window = "30" });

        using var response = await _client.PostAsync("/v1/segments", admin, new { name = "", definition = badDefinition });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("segments.invalid_request", "cheap field checks run before the rule definition is parsed");
    }

    [Fact]
    public async Task Definition_is_stored_and_returned_in_canonical_form()
    {
        var admin = await AdminTokenAsync();
        var raw = JsonSerializer.SerializeToElement(new { type = "event", name = "purchase", cmp = "gte", count = 3.0, window = "30d" });

        using var created = await _client.PostAsync("/v1/segments", admin, new { name = "canonical", definition = raw });

        (await ReadBodyAsync(created)).GetProperty("definition").GetRawText()
            .Should().Be("""{"type":"event","name":"purchase","cmp":"gte","count":3,"window":"30d"}""");
    }

    [Theory]
    [InlineData("seed-purchasers.json", true)]
    [InlineData("seed-pro-id.json", false)]
    public async Task IsRealtimeEligible_matches_the_classifier_and_realtimeMemberCount_is_always_null(string fixtureFile, bool expectedEligible)
    {
        var admin = await AdminTokenAsync();
        var definition = RuleFixtures.LoadDefinition(fixtureFile);

        using var created = await _client.PostAsync("/v1/segments", admin, new { name = fixtureFile, definition });
        var body = await ReadBodyAsync(created);

        body.GetProperty("isRealtimeEligible").GetBoolean().Should().Be(expectedEligible);
        body.GetProperty("realtimeMemberCount").ValueKind.Should().Be(JsonValueKind.Null, "H2: real-time member counts arrive with a later plan");
    }

    [Fact]
    public async Task List_default_hides_archived_status_filter_selects_one_and_a_bad_status_is_400()
    {
        var admin = await AdminTokenAsync();
        var definition = RuleFixtures.LoadDefinition("seed-purchasers.json");

        var draftId = await CreateSegmentAsync(admin, "draft-seg", definition, status: null);
        var activeId = await CreateSegmentAsync(admin, "active-seg", definition, status: "Active");
        var archivedId = await CreateSegmentAsync(admin, "will-be-archived", definition, status: null);
        (await _client.DeleteAsync($"/v1/segments/{archivedId}", admin)).Dispose();

        var defaultList = await _client.ReadAsync<SegmentListResponse>("/v1/segments", admin);
        defaultList.Items.Select(i => i.Id).Should().BeEquivalentTo([draftId, activeId]);

        var archivedList = await _client.ReadAsync<SegmentListResponse>("/v1/segments?status=Archived", admin);
        archivedList.Items.Select(i => i.Id).Should().BeEquivalentTo([archivedId]);

        using var bogus = await _client.GetAsync("/v1/segments?status=bogus", admin);
        bogus.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var bogusBody = JsonDocument.Parse(await bogus.Content.ReadAsStringAsync());
        bogusBody.RootElement.GetProperty("code").GetString().Should().Be("segments.invalid_filter");
    }

    [Theory]
    [InlineData("7")]
    [InlineData("1")]
    public async Task Create_with_a_numeric_status_string_is_400_invalid_request(string status)
    {
        // I-1/M-1: Enum.TryParse alone accepts the string form of any integer — "7" is undefined, "1" is
        // silently Active — neither is one of the three names this field must accept.
        var admin = await AdminTokenAsync();
        var definition = RuleFixtures.LoadDefinition("seed-purchasers.json");

        using var response = await _client.PostAsync("/v1/segments", admin, new { name = "numeric-status-" + status, definition, status });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("segments.invalid_request");
        body.RootElement.GetProperty("errors").TryGetProperty("status", out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("7")]
    [InlineData("1")]
    public async Task Update_with_a_numeric_status_string_is_400_invalid_request(string status)
    {
        var admin = await AdminTokenAsync();
        var definition = RuleFixtures.LoadDefinition("seed-purchasers.json");
        var id = await CreateSegmentAsync(admin, "to-update-" + status, definition, status: null);

        using var response = await _client.PutAsync($"/v1/segments/{id}", admin, new { name = "to-update-" + status, definition, status });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("segments.invalid_request");
        body.RootElement.GetProperty("errors").TryGetProperty("status", out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("7")]
    [InlineData("1")]
    public async Task List_with_a_numeric_status_filter_is_400_invalid_filter(string status)
    {
        // M-1: "?status=1" must not silently mean Active, and "?status=7" must not silently mean "no match".
        var admin = await AdminTokenAsync();

        using var response = await _client.GetAsync($"/v1/segments?status={status}", admin);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("segments.invalid_filter");
    }

    [Fact]
    public async Task List_paging_returns_a_tied_boundary_row_exactly_once_across_pages()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var admin = TenancySeed.AccessToken(_factory, tenant.AdminUserId, tenant.TenantId, Roles.Admin);
        var at = DateTime.UtcNow;

        var first = await SegmentsDb.InsertSegmentAsync(stack.SqlConnectionString, tenant.TenantId, "tie-a", at);
        var second = await SegmentsDb.InsertSegmentAsync(stack.SqlConnectionString, tenant.TenantId, "tie-b", at);

        var page1 = await _client.ReadAsync<SegmentListResponse>("/v1/segments?limit=1", admin);
        page1.Items.Should().HaveCount(1);
        page1.NextCursor.Should().NotBeNull();

        var page2 = await _client.ReadAsync<SegmentListResponse>($"/v1/segments?limit=1&cursor={Uri.EscapeDataString(page1.NextCursor!)}", admin);
        page2.Items.Should().HaveCount(1);

        var ids = page1.Items.Concat(page2.Items).Select(i => i.Id).ToList();
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().BeEquivalentTo([first, second]);
    }

    private async Task<string> AdminTokenAsync()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        return TenancySeed.AccessToken(_factory, tenant.AdminUserId, tenant.TenantId, Roles.Admin);
    }

    private async Task<Guid> CreateSegmentAsync(string bearer, string name, JsonElement definition, string? status)
    {
        using var response = await _client.PostAsync("/v1/segments", bearer, new { name, definition, status });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await ReadBodyAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ReadBodyAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

}
