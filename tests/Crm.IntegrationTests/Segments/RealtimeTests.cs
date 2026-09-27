using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Crm.Contracts;
using Crm.IntegrationTests.Fixtures;
using Crm.IntegrationTests.Ingestion;
using Crm.IntegrationTests.Profiles;
using Crm.Kernel.Redis;
using Crm.Segmentation.Application.Segments;
using Crm.Segmentation.Infrastructure.Realtime;
using Crm.Tenancy.Domain;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace Crm.IntegrationTests.Segments;

/// <summary>
/// IT-022…028 (plan I): real-time segment evaluation through the real pipeline — API `track`/`identify` →
/// `events.raw` → the profiles consumer → `events.processed` → the segmentation consumer — asserting on the Redis
/// set and counters directly and on `segments.MembershipChanges`. IT-024 drives <see cref="RealtimeMessageHandler"/>
/// directly (ruling I14) so it can age a counter's TTL by hand between two messages.
/// </summary>
[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class RealtimeTests(CrmStackFixture stack) : IAsyncLifetime
{
    private readonly CrmApiFactory _factory = new(stack);
    private readonly List<Guid> _tenantIds = [];
    private HttpClient _client = null!;
    private ConnectionMultiplexer _redis = null!;

    /// <summary>NFR-02: 2 s locally; 6 s in CI or against a remote engine (docs/test/integration.md: 3x multiplier).</summary>
    private static TimeSpan Budget
        => Environment.GetEnvironmentVariable("CI") == "true" || Environment.GetEnvironmentVariable("TESTCONTAINERS_HOST_OVERRIDE") is { Length: > 0 }
            ? TimeSpan.FromSeconds(6)
            : TimeSpan.FromSeconds(2);

    public async Task InitializeAsync()
    {
        _client = _factory.CreateApiClient();
        _redis = await ConnectionMultiplexer.ConnectAsync(stack.RedisConfiguration);
    }

    public async Task DisposeAsync()
    {
        foreach (var tenantId in _tenantIds)
        {
            await SeedDataset.DeleteAsync(stack.SqlConnectionString, tenantId);
        }

        _client.Dispose();
        await _redis.DisposeAsync();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IT022_qualifying_event_adds_the_profile_to_the_set()
    {
        var t = await TenantAsync();
        var segmentId = await CreateSegmentAsync(t, "two-purchases", """{"type":"event","name":"purchase","cmp":"gte","count":2,"window":"30d"}""");

        await TrackAsync(t, "u1", "purchase");
        var profileId = await ProfileIdAsync(t, "u1");
        await CounterEqualsAsync(t, segmentId, "purchase", "30d", profileId, 1);
        (await IsMemberAsync(t, segmentId, profileId)).Should().BeFalse("one purchase is not two");

        var sent = Stopwatch.StartNew();
        await TrackAsync(t, "u1", "purchase");
        await Eventually.TrueAsync(() => IsMemberAsync(t, segmentId, profileId), "the second purchase admits u1", Budget * 5);
        sent.Elapsed.Should().BeLessThan(Budget, "NFR-02: real-time membership within 2 s");

        await Eventually.TrueAsync(
            async () => (await SegmentsDb.ListChangesAsync(stack.SqlConnectionString, t.TenantId, segmentId)).Count == 1,
            "the Entered change is recorded");
        (await SegmentsDb.ListChangesAsync(stack.SqlConnectionString, t.TenantId, segmentId))
            .Should().Equal((profileId, "Entered", "Realtime"));
    }

    [Fact]
    public async Task IT023_attribute_change_removes_the_profile_from_the_set()
    {
        var t = await TenantAsync();
        var segmentId = await CreateSegmentAsync(t, "pros", """{"type":"attribute","key":"plan","cmp":"eq","value":"pro"}""");

        await IdentifyAsync(t, "u2", new { plan = "pro" });
        var profileId = await ProfileIdAsync(t, "u2");
        await Eventually.TrueAsync(() => IsMemberAsync(t, segmentId, profileId), "plan=pro admits u2");

        await IdentifyAsync(t, "u2", new { plan = "free" });
        await Eventually.TrueAsync(async () => !await IsMemberAsync(t, segmentId, profileId), "plan=free removes u2");

        await Eventually.TrueAsync(
            async () => (await SegmentsDb.ListChangesAsync(stack.SqlConnectionString, t.TenantId, segmentId)).Count == 2,
            "both transitions are recorded");
        (await SegmentsDb.ListChangesAsync(stack.SqlConnectionString, t.TenantId, segmentId))
            .Should().Equal((profileId, "Entered", "Realtime"), (profileId, "Exited", "Realtime"));
    }

    [Fact]
    public async Task IT024_counter_ttl_is_set_once_not_re_armed()
    {
        var t = await TenantAsync();
        var segmentId = await CreateSegmentAsync(t, "three-in-a-week", """{"type":"event","name":"purchase","cmp":"gte","count":3,"window":"7d"}""");
        var profileId = Guid.NewGuid();
        var key = RedisKeys.EventCounter(t.TenantId, segmentId, "purchase", "7d", profileId);
        var db = _redis.GetDatabase();

        await HandleDirectlyAsync(Tracked(t.TenantId, profileId, "purchase", DateTime.UtcNow));
        (await db.StringGetAsync(key)).Should().Be(1);
        (await db.KeyTimeToLiveAsync(key)).Should().BeCloseTo(TimeSpan.FromDays(7), TimeSpan.FromMinutes(1));

        // Six days and 23 hours later, as far as the key is concerned.
        await db.KeyExpireAsync(key, TimeSpan.FromHours(1));
        await HandleDirectlyAsync(Tracked(t.TenantId, profileId, "purchase", DateTime.UtcNow));

        (await db.StringGetAsync(key)).Should().Be(2);
        (await db.KeyTimeToLiveAsync(key)).Should().BeLessThanOrEqualTo(TimeSpan.FromHours(1), "EXPIRE NX never re-arms a live counter");
    }

    [Fact]
    public async Task I3_an_event_older_than_the_window_is_not_counted()
    {
        var t = await TenantAsync();
        var segmentId = await CreateSegmentAsync(t, "one-in-a-day", """{"type":"event","name":"purchase","cmp":"gte","count":1,"window":"24h"}""");
        var profileId = Guid.NewGuid();

        await HandleDirectlyAsync(Tracked(t.TenantId, profileId, "purchase", DateTime.UtcNow.AddDays(-2)));

        (await _redis.GetDatabase().KeyExistsAsync(RedisKeys.EventCounter(t.TenantId, segmentId, "purchase", "24h", profileId))).Should().BeFalse();
        (await IsMemberAsync(t, segmentId, profileId)).Should().BeFalse();
    }

    [Fact]
    public async Task IT025_multi_event_segments_keep_separate_counters()
    {
        var t = await TenantAsync();
        var segmentId = await CreateSegmentAsync(t, "buyers-who-open", """
            {"op":"and","children":[
              {"type":"event","name":"purchase","cmp":"gte","count":2,"window":"30d"},
              {"type":"event","name":"app_open","cmp":"gte","count":1,"window":"7d"}]}
            """);

        await TrackAsync(t, "u3", "purchase");
        await TrackAsync(t, "u3", "purchase");
        var profileId = await ProfileIdAsync(t, "u3");
        await CounterEqualsAsync(t, segmentId, "purchase", "30d", profileId, 2);
        (await IsMemberAsync(t, segmentId, profileId)).Should().BeFalse("no app_open yet");
        (await _redis.GetDatabase().KeyExistsAsync(RedisKeys.EventCounter(t.TenantId, segmentId, "app_open", "7d", profileId))).Should().BeFalse();

        await TrackAsync(t, "u3", "app_open");
        await Eventually.TrueAsync(() => IsMemberAsync(t, segmentId, profileId), "the app_open completes the rule");

        var db = _redis.GetDatabase();
        (await db.StringGetAsync(RedisKeys.EventCounter(t.TenantId, segmentId, "purchase", "30d", profileId))).Should().Be(2);
        (await db.StringGetAsync(RedisKeys.EventCounter(t.TenantId, segmentId, "app_open", "7d", profileId))).Should().Be(1);
    }

    [Fact]
    public async Task IT027_saving_a_segment_invalidates_the_worker_cache()
    {
        var t = await TenantAsync();
        var segmentId = await CreateSegmentAsync(t, "tier-rule", """{"type":"attribute","key":"plan","cmp":"eq","value":"pro"}""");

        // Warm the Worker's cache with the old rule: u-pro becoming a member proves the tenant's list is loaded.
        await IdentifyAsync(t, "u-pro", new { plan = "pro" });
        var pro = await ProfileIdAsync(t, "u-pro");
        await Eventually.TrueAsync(() => IsMemberAsync(t, segmentId, pro), "the old rule is cached and applied");

        var published = Channel.CreateUnbounded<string>();
        var subscriber = _redis.GetSubscriber();
        await subscriber.SubscribeAsync(RedisChannel.Literal(RedisKeys.SegmentsChangedChannel), (_, message) => published.Writer.TryWrite(message.ToString()));
        try
        {
            using var put = await _client.PutAsync($"/v1/segments/{segmentId}", t.Admin, new
            {
                name = "tier-rule",
                status = "Active",
                definition = JsonDocument.Parse("""{"type":"attribute","key":"plan","cmp":"eq","value":"gold"}""").RootElement,
            });
            put.StatusCode.Should().Be(HttpStatusCode.OK);

            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string message;
            do
            {
                message = await published.Reader.ReadAsync(wait.Token);
            }
            while (message != t.TenantId.ToString("N"));
        }
        finally
        {
            await subscriber.UnsubscribeAsync(RedisChannel.Literal(RedisKeys.SegmentsChangedChannel));
        }

        var sent = Stopwatch.StartNew();
        await IdentifyAsync(t, "u-gold", new { plan = "gold" });
        var gold = await Eventually.GetAsync(() => ProfileIdOrNullAsync(t, "u-gold"), "u-gold is created");
        await Eventually.TrueAsync(() => IsMemberAsync(t, segmentId, gold.Id), "the new rule applies", Budget * 5);
        sent.Elapsed.Should().BeLessThan(Budget + Budget, "the new rule applies without waiting for the cache TTL (60 s)");
    }

    [Fact]
    public async Task IT026_batch_run_rebuilds_the_real_time_set()
    {
        var t = await TenantAsync();
        var pros = await ProfilesWithPlanAsync(t, "pro", 3);
        await ProfilesWithPlanAsync(t, "free", 1);
        var segmentId = await CreateSegmentAsync(t, "rebuilt", """{"type":"attribute","key":"plan","cmp":"eq","value":"pro"}""");
        var key = RedisKeys.SegmentMembers(t.TenantId, segmentId);
        var polluted = Guid.NewGuid();
        await _redis.GetDatabase().SetAddAsync(key, polluted.ToString("N"));

        await RunToFinishAsync(t, segmentId);

        var members = await _redis.GetDatabase().SetMembersAsync(key);
        members.Select(m => Guid.ParseExact(m.ToString(), "N")).Should().BeEquivalentTo(pros);
        var detail = await _client.ReadAsync<SegmentResponse>($"/v1/segments/{segmentId}", t.Admin);
        detail.MemberCount.Should().Be(3);
        detail.RealtimeMemberCount.Should().Be(3, "SCARD = MemberCount after a rebuild, the polluted id is gone");
    }

    [Fact]
    public async Task IT028_segment_detail_exposes_both_member_counts()
    {
        var t = await TenantAsync();
        await ProfilesWithPlanAsync(t, "pro", 2);
        var eligible = await CreateSegmentAsync(t, "both-counts", """{"type":"attribute","key":"plan","cmp":"eq","value":"pro"}""");
        var ineligible = await CreateSegmentAsync(t, "batch-only", """{"op":"not","children":[{"type":"attribute","key":"plan","cmp":"eq","value":"pro"}]}""", expectEligible: false);
        await RunToFinishAsync(t, eligible);
        await RunToFinishAsync(t, ineligible);

        // One real-time member since the run.
        await _redis.GetDatabase().SetAddAsync(RedisKeys.SegmentMembers(t.TenantId, eligible), Guid.NewGuid().ToString("N"));

        var detail = await _client.ReadAsync<SegmentResponse>($"/v1/segments/{eligible}", t.Admin);
        detail.MemberCount.Should().Be(2);
        detail.RealtimeMemberCount.Should().Be(3);

        var batchOnly = await _client.ReadAsync<SegmentResponse>($"/v1/segments/{ineligible}", t.Admin);
        batchOnly.IsRealtimeEligible.Should().BeFalse();
        batchOnly.RealtimeMemberCount.Should().BeNull();
    }

    [Fact]
    public async Task I13_a_set_nothing_maintains_is_deleted()
    {
        var t = await TenantAsync();
        await ProfilesWithPlanAsync(t, "pro", 1);
        var archived = await CreateSegmentAsync(t, "to-archive", """{"type":"attribute","key":"plan","cmp":"eq","value":"pro"}""");
        var drafted = await CreateSegmentAsync(t, "to-draft", """{"type":"attribute","key":"plan","cmp":"eq","value":"pro"}""");
        await RunToFinishAsync(t, archived);
        await RunToFinishAsync(t, drafted);
        var db = _redis.GetDatabase();
        (await db.KeyExistsAsync(RedisKeys.SegmentMembers(t.TenantId, archived))).Should().BeTrue();
        (await db.KeyExistsAsync(RedisKeys.SegmentMembers(t.TenantId, drafted))).Should().BeTrue();

        using (var delete = await _client.DeleteAsync($"/v1/segments/{archived}", t.Admin))
        {
            delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        using (var put = await _client.PutAsync($"/v1/segments/{drafted}", t.Admin, new
        {
            name = "to-draft",
            status = "Draft",
            definition = JsonDocument.Parse("""{"type":"attribute","key":"plan","cmp":"eq","value":"pro"}""").RootElement,
        }))
        {
            put.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await db.KeyExistsAsync(RedisKeys.SegmentMembers(t.TenantId, archived))).Should().BeFalse();
        (await db.KeyExistsAsync(RedisKeys.SegmentMembers(t.TenantId, drafted))).Should().BeFalse();
        (await _client.ReadAsync<SegmentResponse>($"/v1/segments/{drafted}", t.Admin)).RealtimeMemberCount.Should().BeNull("a Draft segment's set is not maintained");
    }

    [Fact]
    public async Task I12_detail_answers_null_when_redis_is_down()
    {
        var t = await TenantAsync();
        var segmentId = await CreateSegmentAsync(t, "redis-down", """{"type":"attribute","key":"plan","cmp":"eq","value":"pro"}""");

        await stack.PauseRedisAsync();
        try
        {
            using var response = await _client.GetAsync($"/v1/segments/{segmentId}", t.Admin);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadFromJsonAsync<SegmentResponse>())!.RealtimeMemberCount.Should().BeNull();
        }
        finally
        {
            await stack.UnpauseRedisAsync();
        }
    }

    private async Task<IReadOnlyList<Guid>> ProfilesWithPlanAsync(Tenant t, string plan, int count)
    {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var id = await ProfilesDb.InsertProfileAsync(stack.SqlConnectionString, t.TenantId, $"{plan}-{i}-{Guid.NewGuid():N}", DateTime.UtcNow);
            await ProfilesDb.SetPlanAsync(stack.SqlConnectionString, t.TenantId, id, plan);
            ids.Add(id);
        }

        return ids;
    }

    private async Task RunToFinishAsync(Tenant t, Guid segmentId)
    {
        using var response = await _client.PostAsync($"/v1/segments/{segmentId}/run", t.Admin, new { });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var runId = (await response.Content.ReadFromJsonAsync<RunQueuedResponse>())!.RunId;
        await Eventually.TrueAsync(
            async () => (await _client.ReadAsync<SegmentResponse>($"/v1/segments/{segmentId}", t.Admin)).CurrentRunId == runId,
            $"run {runId} finishes");
    }

    private async Task<Tenant> TenantAsync()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        _tenantIds.Add(tenant.TenantId);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        var admin = TenancySeed.AccessToken(_factory, tenant.AdminUserId, tenant.TenantId, Roles.Admin);
        return new Tenant(tenant.TenantId, key.Plaintext, admin);
    }

    private async Task<Guid> CreateSegmentAsync(Tenant t, string name, string definitionJson, bool expectEligible = true)
    {
        using var definition = JsonDocument.Parse(definitionJson);
        using var response = await _client.PostAsync("/v1/segments", t.Admin, new { name, status = "Active", definition = definition.RootElement });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("isRealtimeEligible").GetBoolean().Should().Be(expectEligible);
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private async Task TrackAsync(Tenant t, string userId, string @event)
    {
        using var response = await _client.PostWithKeyAsync("/v1/track", t.ApiKey, new { userId, @event });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    private async Task IdentifyAsync(Tenant t, string userId, object traits)
    {
        using var response = await _client.PostWithKeyAsync("/v1/identify", t.ApiKey, new { userId, traits });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    private async Task<Guid> ProfileIdAsync(Tenant t, string userId)
        => (await Eventually.GetAsync(() => ProfileIdOrNullAsync(t, userId), $"profile {userId} is created")).Id;

    private async Task<ProfileRef?> ProfileIdOrNullAsync(Tenant t, string userId)
        => await ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, t.TenantId, "external_id", userId) is { } row ? new ProfileRef(row.Id) : null;

    private Task<bool> IsMemberAsync(Tenant t, Guid segmentId, Guid profileId)
        => _redis.GetDatabase().SetContainsAsync(RedisKeys.SegmentMembers(t.TenantId, segmentId), profileId.ToString("N"));

    private Task CounterEqualsAsync(Tenant t, Guid segmentId, string eventName, string window, Guid profileId, long expected)
        => Eventually.TrueAsync(
            async () => (long?)await _redis.GetDatabase().StringGetAsync(RedisKeys.EventCounter(t.TenantId, segmentId, eventName, window, profileId)) == expected,
            $"counter {eventName}:{window} reaches {expected}");

    private async Task HandleDirectlyAsync(ProcessedEnvelope envelope)
    {
        await using var scope = stack.Worker.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RealtimeMessageHandler>().HandleAsync(envelope, CancellationToken.None);
    }

    private static ProcessedEnvelope Tracked(Guid tenantId, Guid profileId, string @event, DateTime occurredAt)
    {
        using var attributes = JsonDocument.Parse("{}");
        var payload = new EventTracked(Guid.NewGuid(), tenantId, profileId, @event, attributes.RootElement.Clone(), occurredAt,
            new ProfileSnapshot(null, null, null, attributes.RootElement.Clone()));
        var headers = new Dictionary<string, string>
        {
            [KafkaHeaders.TenantId] = tenantId.ToString("N"),
            [KafkaHeaders.MessageType] = nameof(EventTracked),
        };
        return new ProcessedEnvelope(KafkaTopics.EventsProcessed, 0, 0, $"{tenantId:N}:{profileId:N}", JsonSerializer.SerializeToUtf8Bytes(payload, ContractJson.Options), headers);
    }

    private sealed record Tenant(Guid TenantId, string ApiKey, string Admin);

    private sealed record ProfileRef(Guid Id);
}
