using System.Text.Json;
using Crm.Contracts;
using Crm.Profiles.Application.Messages;
using Crm.Profiles.Application.Pipeline;
using Crm.Profiles.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class ProfilePipelineTrackTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTime T0 = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);
    private readonly InMemoryProfileStore _store = new();

    private ProfilePipeline Pipeline() => new(_store, new NoPipelineHooks());

    internal static TrackMessage Track(Guid tenant, string? userId, string? anonymousId, DateTime at, Guid? eventId = null, string name = "purchase")
        => new(new TrackEventRaw(eventId ?? Guid.CreateVersion7(), tenant, userId, anonymousId, name, JsonDocument.Parse("""{"amount":49.9}""").RootElement.Clone(), at, at));

    [Fact]
    public async Task First_track_creates_profile_identity_and_event_inside_one_transaction()
    {
        var outcome = await Pipeline().HandleAsync(Track(Tenant, "u1", null, T0), CancellationToken.None);

        var profile = _store.ProfileOf(IdentityKey.ExternalId("u1"));
        profile.ExternalId.Should().Be("u1");
        profile.EventCount.Should().Be(1);
        profile.FirstSeenAt.Should().Be(T0);
        _store.Events.Should().ContainSingle().Which.ProfileId.Should().Be(profile.Id);
        _store.Calls.First().Should().Be("begin");
        _store.Calls.Last().Should().Be("commit");
        _store.Remembered[IdentityKey.ExternalId("u1")].Should().Be(profile.Id);
        outcome.Should().BeEquivalentTo(new { ProfileId = profile.Id, Duplicate = false, Redirected = false, Merged = false });
    }

    [Fact]
    public async Task EventTracked_is_keyed_by_tenant_and_profile_and_carries_the_snapshot()
    {
        var message = Track(Tenant, "u1", null, T0);

        var outcome = await Pipeline().HandleAsync(message, CancellationToken.None);

        var processed = outcome.Messages.Should().ContainSingle().Subject;
        processed.MessageType.Should().Be(nameof(EventTracked));
        processed.TenantId.Should().Be(Tenant);
        processed.Key.Should().Be($"{Tenant:N}:{outcome.ProfileId:N}");
        var payload = JsonSerializer.Deserialize<EventTracked>(processed.Value, ContractJson.Options)!;
        payload.EventId.Should().Be(message.Payload.EventId);
        payload.ProfileId.Should().Be(outcome.ProfileId);
        payload.Event.Should().Be("purchase");
        payload.Properties.GetProperty("amount").GetDouble().Should().Be(49.9);
        payload.Profile.ExternalId.Should().Be("u1");
        payload.Profile.Attributes.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task FR06_a_duplicate_event_writes_nothing_but_is_announced_again()
    {
        var message = Track(Tenant, "u1", null, T0);
        var first = await Pipeline().HandleAsync(message, CancellationToken.None);

        var second = await Pipeline().HandleAsync(message, CancellationToken.None);

        second.Duplicate.Should().BeTrue();
        _store.Events.Should().ContainSingle();
        _store.ProfileOf(IdentityKey.ExternalId("u1")).EventCount.Should().Be(1);
        // Ruling E7: the first delivery may have died between the SQL commit and the produce.
        second.Messages.Should().ContainSingle().Which.MessageType.Should().Be(nameof(EventTracked));
        second.Messages[0].Key.Should().Be(first.Messages[0].Key);
    }

    [Fact]
    public async Task Absent_properties_are_stored_as_an_empty_object()
    {
        var raw = Track(Tenant, "u1", null, T0).Payload with { Properties = default };

        await Pipeline().HandleAsync(new TrackMessage(raw), CancellationToken.None);

        _store.Events.Single().Properties.Should().Be("{}");
    }

    [Fact]
    public async Task E5_track_with_both_ids_attaches_the_unmapped_anonymous_id_to_the_known_profile()
    {
        await Pipeline().HandleAsync(Track(Tenant, "u1", null, T0), CancellationToken.None);

        await Pipeline().HandleAsync(Track(Tenant, "u1", "a1", T0.AddMinutes(1)), CancellationToken.None);

        _store.Profiles.Should().ContainSingle();
        _store.ProfileOf(IdentityKey.AnonymousId("a1")).ExternalId.Should().Be("u1");
        _store.ProfileOf(IdentityKey.ExternalId("u1")).EventCount.Should().Be(2);
    }

    [Fact]
    public async Task E5_track_with_both_ids_promotes_an_anonymous_profile_that_has_no_external_id()
    {
        await Pipeline().HandleAsync(Track(Tenant, null, "a1", T0), CancellationToken.None);

        await Pipeline().HandleAsync(Track(Tenant, "u1", "a1", T0.AddMinutes(1)), CancellationToken.None);

        _store.Profiles.Should().ContainSingle();
        _store.ProfileOf(IdentityKey.ExternalId("u1")).EventCount.Should().Be(2);
    }

    [Fact]
    public async Task E5_track_never_merges_two_existing_profiles()
    {
        await Pipeline().HandleAsync(Track(Tenant, null, "a1", T0), CancellationToken.None);
        await Pipeline().HandleAsync(Track(Tenant, "u1", null, T0.AddMinutes(1)), CancellationToken.None);

        var outcome = await Pipeline().HandleAsync(Track(Tenant, "u1", "a1", T0.AddMinutes(2)), CancellationToken.None);

        outcome.Merged.Should().BeFalse();
        _store.Profiles.Should().HaveCount(2);
        outcome.ProfileId.Should().Be(_store.ProfileOf(IdentityKey.ExternalId("u1")).Id);
        _store.ProfileOf(IdentityKey.AnonymousId("a1")).EventCount.Should().Be(1);
    }

    [Fact]
    public async Task UT013_an_event_that_resolves_to_a_merge_loser_is_applied_to_the_winner()
    {
        await Pipeline().HandleAsync(Track(Tenant, null, "a1", T0), CancellationToken.None);
        await Pipeline().HandleAsync(Track(Tenant, "u1", null, T0.AddMinutes(1)), CancellationToken.None);
        var winner = _store.ProfileOf(IdentityKey.AnonymousId("a1"));
        var loser = _store.ProfileOf(IdentityKey.ExternalId("u1"));
        ProfileMerge.Apply(winner, loser, _store.Identities.Where(i => i.ProfileId == loser.Id).ToList());
        _store.Cache[IdentityKey.ExternalId("u1")] = loser.Id; // what a reader cached just before the merge committed

        var outcome = await Pipeline().HandleAsync(Track(Tenant, "u1", null, T0.AddMinutes(2)), CancellationToken.None);

        outcome.Redirected.Should().BeTrue();
        outcome.ProfileId.Should().Be(winner.Id);
        winner.EventCount.Should().Be(2);
        _store.Forgotten.Should().Contain(IdentityKey.ExternalId("u1"));
        _store.Remembered[IdentityKey.ExternalId("u1")].Should().Be(winner.Id);
    }

    [Fact]
    public async Task A_cached_id_whose_profile_is_gone_falls_back_to_the_database()
    {
        await Pipeline().HandleAsync(Track(Tenant, "u1", null, T0), CancellationToken.None);
        _store.Cache[IdentityKey.ExternalId("u1")] = Guid.CreateVersion7();

        var outcome = await Pipeline().HandleAsync(Track(Tenant, "u1", null, T0.AddMinutes(1)), CancellationToken.None);

        outcome.ProfileId.Should().Be(_store.ProfileOf(IdentityKey.ExternalId("u1")).Id);
        _store.Profiles.Should().ContainSingle();
        _store.Forgotten.Should().Contain(IdentityKey.ExternalId("u1"));
    }
}
