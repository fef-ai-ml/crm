using System.Text.Json;
using Crm.Contracts;
using Crm.IntegrationTests.Fixtures;
using Crm.Kernel.Data;
using Crm.Kernel.Redis;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Profiles.Application.Messages;
using Crm.Profiles.Application.Pipeline;
using Crm.Profiles.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace Crm.IntegrationTests.Profiles;

/// <summary>The store against real SQL Server and Redis, without Kafka and without a host: the module plus the services a host provides.</summary>
[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class ProfileStoreTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);
    private readonly CrmStackFixture _stack;
    private readonly ServiceProvider _provider;
    private readonly Guid _tenant = Guid.CreateVersion7();

    public ProfileStoreTests(CrmStackFixture stack)
    {
        _stack = stack;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Crm"] = stack.SqlConnectionString })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(stack.RedisConfiguration));
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<TenantSaveChangesInterceptor>();
        services.AddProfilesModule(configuration);
        _provider = services.BuildServiceProvider(validateScopes: true);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private AsyncServiceScope Scope(Guid tenant)
    {
        var scope = _provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(tenant, null, TenantPrincipalKind.None);
        return scope;
    }

    private static TrackMessage Track(Guid tenant, string userId, Guid eventId, DateTime at)
        => new(new TrackEventRaw(eventId, tenant, userId, null, "purchase", JsonDocument.Parse("""{"amount":1}""").RootElement.Clone(), at, at));

    private async Task<ProcessingOutcome> HandleAsync(Guid tenant, RawMessage message)
    {
        await using var scope = Scope(tenant);
        return await scope.ServiceProvider.GetRequiredService<ProfilePipeline>().HandleAsync(message, CancellationToken.None);
    }

    [Fact]
    public async Task A_track_is_persisted_once_however_often_it_is_delivered()
    {
        var eventId = Guid.CreateVersion7();
        var occurredWithTicks = T0.AddTicks(1_234_567); // more precise than datetime2(3): ruling E4

        var first = await HandleAsync(_tenant, Track(_tenant, "u1", eventId, occurredWithTicks));
        var second = await HandleAsync(_tenant, Track(_tenant, "u1", eventId, occurredWithTicks));

        first.Duplicate.Should().BeFalse();
        second.Duplicate.Should().BeTrue();
        second.ProfileId.Should().Be(first.ProfileId);
        await using var scope = Scope(_tenant);
        var db = scope.ServiceProvider.GetRequiredService<ProfilesDbContext>();
        (await db.Events.CountAsync()).Should().Be(1);
        var profile = await db.Profiles.SingleAsync();
        profile.EventCount.Should().Be(1);
        profile.ExternalId.Should().Be("u1");
        profile.Attributes.Should().Be("{}");
        profile.CreatedAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-5));
        (await db.UserIdentities.SingleAsync()).ProfileId.Should().Be(profile.Id);
    }

    [Fact]
    public async Task The_identity_is_cached_after_the_commit_with_a_one_hour_ttl()
    {
        var outcome = await HandleAsync(_tenant, Track(_tenant, "cached-user", Guid.CreateVersion7(), T0));

        var redis = _provider.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        var key = RedisKeys.IdentityCache(_tenant, "external_id", "cached-user");
        (await redis.StringGetAsync(key)).ToString().Should().Be(outcome.ProfileId.ToString("N"));
        var ttl = await redis.KeyTimeToLiveAsync(key);
        ttl.Should().NotBeNull();
        ttl!.Value.Should().BeCloseTo(TimeSpan.FromHours(1), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task A_rolled_back_message_leaves_no_rows_and_no_cache_entry()
    {
        await using (var scope = Scope(_tenant))
        {
            var store = scope.ServiceProvider.GetRequiredService<IProfileStore>();
            await store.BeginAsync(CancellationToken.None);
            var profile = Crm.Profiles.Domain.Profile.Create(_tenant, T0);
            store.Add(profile);
            store.Remember(Crm.Profiles.Domain.IdentityKey.ExternalId("ghost"), profile.Id);
            // no CommitAsync: disposing the scope rolls back
        }

        await using var verify = Scope(_tenant);
        (await verify.ServiceProvider.GetRequiredService<ProfilesDbContext>().Profiles.CountAsync()).Should().Be(0);
        var redis = _provider.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        (await redis.KeyExistsAsync(RedisKeys.IdentityCache(_tenant, "external_id", "ghost"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_locked_profile_blocks_a_second_locker_until_the_first_commits()
    {
        var profileId = (await HandleAsync(_tenant, Track(_tenant, "locked-user", Guid.CreateVersion7(), T0))).ProfileId;

        await using var first = Scope(_tenant);
        var holder = first.ServiceProvider.GetRequiredService<IProfileStore>();
        await holder.BeginAsync(CancellationToken.None);
        (await holder.LockProfileAsync(profileId, CancellationToken.None)).Should().NotBeNull();

        await using var second = Scope(_tenant);
        var waiter = second.ServiceProvider.GetRequiredService<IProfileStore>();
        await waiter.BeginAsync(CancellationToken.None);
        var waiting = waiter.LockProfileAsync(profileId, CancellationToken.None);

        (await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(2)))).Should().NotBeSameAs(waiting, "UPDLOCK, HOLDLOCK must serialize writers of one profile");
        await holder.CommitAsync(CancellationToken.None);
        (await waiting.WaitAsync(TimeSpan.FromSeconds(30))).Should().NotBeNull();
        await waiter.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NFR03_another_tenant_cannot_lock_or_find_the_profile()
    {
        var outcome = await HandleAsync(_tenant, Track(_tenant, "tenant-a-user", Guid.CreateVersion7(), T0));
        var otherTenant = Guid.CreateVersion7();

        await using var scope = Scope(otherTenant);
        var store = scope.ServiceProvider.GetRequiredService<IProfileStore>();
        await store.BeginAsync(CancellationToken.None);

        (await store.LockProfileAsync(outcome.ProfileId, CancellationToken.None)).Should().BeNull();
        (await store.FindProfileIdAsync(Crm.Profiles.Domain.IdentityKey.ExternalId("tenant-a-user"), bypassCache: false, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task NFR03_appending_an_event_built_for_another_tenant_throws_and_is_never_counted()
    {
        var outcome = await HandleAsync(_tenant, Track(_tenant, "tenant-a-user", Guid.CreateVersion7(), T0));
        var otherTenant = Guid.CreateVersion7();

        await using var scope = Scope(otherTenant);
        var store = scope.ServiceProvider.GetRequiredService<IProfileStore>();
        await store.BeginAsync(CancellationToken.None);

        var record = new Crm.Profiles.Domain.EventRecord(Guid.CreateVersion7(), _tenant, outcome.ProfileId, "purchase", "{}", T0, T0);
        var act = () => store.TryAppendEventAsync(record, CancellationToken.None);

        await act.Should().ThrowAsync<TenantMismatchException>();
        (await store.CountEventsAsync(outcome.ProfileId, CancellationToken.None)).Should().Be(0, "CountEventsAsync also filters by the scope's tenant");
    }
}
