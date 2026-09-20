using System.Text.Json;
using Crm.Contracts;
using Crm.Profiles.Application.Messages;
using Crm.Profiles.Application.Pipeline;
using Crm.Profiles.Domain;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class ProfilePipelineIdentifyTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTime T0 = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);
    private readonly InMemoryProfileStore _store = new();
    private readonly IPipelineHooks _hooks = Substitute.For<IPipelineHooks>();

    private ProfilePipeline Pipeline() => new(_store, _hooks);

    private static IdentifyMessage Identify(string userId, string? anonymousId, string traits, DateTime at)
        => new(new IdentifyRaw(Guid.CreateVersion7(), Tenant, userId, anonymousId, JsonDocument.Parse(traits).RootElement.Clone(), at));

    private Task<ProcessingOutcome> TrackAsync(string? userId, string? anonymousId, DateTime at)
        => Pipeline().HandleAsync(ProfilePipelineTrackTests.Track(Tenant, userId, anonymousId, at), CancellationToken.None);

    [Fact]
    public async Task IT002_shape_identify_creates_a_profile_applies_traits_and_never_counts_as_an_event()
    {
        await Pipeline().HandleAsync(Identify("u2", null, """{"plan":"pro"}""", T0), CancellationToken.None);
        var outcome = await Pipeline().HandleAsync(Identify("u2", null, """{"plan":"free","city":"SG"}""", T0.AddMinutes(1)), CancellationToken.None);

        var profile = _store.ProfileOf(IdentityKey.ExternalId("u2"));
        _store.Profiles.Should().ContainSingle();
        profile.Attributes.Should().Be("""{"plan":"free","city":"SG"}""");
        profile.EventCount.Should().Be(0);
        profile.LastSeenAt.Should().Be(T0.AddMinutes(1));
        _store.Events.Should().BeEmpty();
        var updated = JsonSerializer.Deserialize<ProfileUpdated>(outcome.Messages.Should().ContainSingle().Subject.Value, ContractJson.Options)!;
        updated.ChangedKeys.Should().Equal("plan", "city");
        updated.ProfileId.Should().Be(profile.Id);
        updated.OccurredAt.Should().Be(T0.AddMinutes(1));
    }

    [Fact]
    public async Task An_identify_that_changes_nothing_on_an_existing_profile_publishes_nothing()
    {
        await Pipeline().HandleAsync(Identify("u2", null, """{"plan":"pro"}""", T0), CancellationToken.None);

        var outcome = await Pipeline().HandleAsync(Identify("u2", null, """{"plan":"pro"}""", T0.AddMinutes(1)), CancellationToken.None);

        outcome.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task IT007_shape_identify_merges_the_anonymous_profile_into_the_known_one_older_wins()
    {
        await TrackAsync(null, "a1", T0);
        await TrackAsync(null, "a1", T0.AddMinutes(1));
        await TrackAsync("u1", null, T0.AddMinutes(2));
        var p1 = _store.ProfileOf(IdentityKey.AnonymousId("a1"));
        var p2 = _store.ProfileOf(IdentityKey.ExternalId("u1"));

        var outcome = await Pipeline().HandleAsync(Identify("u1", "a1", """{"plan":"pro"}""", T0.AddMinutes(3)), CancellationToken.None);

        outcome.Merged.Should().BeTrue();
        outcome.ProfileId.Should().Be(p1.Id);
        p2.MergedIntoId.Should().Be(p1.Id);
        p1.ExternalId.Should().Be("u1");
        p1.EventCount.Should().Be(3, "recomputed as COUNT(*) of the winner's events (IT-010)");
        _store.EventOwners.Values.Should().OnlyContain(owner => owner == p1.Id);
        _store.Identities.Should().OnlyContain(i => i.ProfileId == p1.Id);
        _store.Forgotten.Should().Contain(IdentityKey.ExternalId("u1"), "the loser's identities leave the cache before the commit (IT-009)");
        await _hooks.Received(1).MergeLocksTakenAsync(Tenant, Arg.Any<CancellationToken>());

        outcome.Messages.Select(m => m.MessageType).Should().Equal(nameof(ProfileMerged), nameof(ProfileUpdated));
        var merged = JsonSerializer.Deserialize<ProfileMerged>(outcome.Messages[0].Value, ContractJson.Options)!;
        merged.WinnerProfileId.Should().Be(p1.Id);
        merged.LoserProfileId.Should().Be(p2.Id);
        outcome.Messages[0].Key.Should().Be($"{Tenant:N}:{p1.Id:N}");
    }

    [Fact]
    public async Task E5_an_anonymous_profile_that_already_belongs_to_another_user_is_not_merged()
    {
        await TrackAsync("u0", "a1", T0);
        await TrackAsync("u1", null, T0.AddMinutes(1));

        var outcome = await Pipeline().HandleAsync(Identify("u1", "a1", "{}", T0.AddMinutes(2)), CancellationToken.None);

        outcome.Merged.Should().BeFalse();
        outcome.ProfileId.Should().Be(_store.ProfileOf(IdentityKey.ExternalId("u1")).Id);
        _store.ProfileOf(IdentityKey.AnonymousId("a1")).ExternalId.Should().Be("u0");
        await _hooks.DidNotReceive().MergeLocksTakenAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Identify_links_an_anonymous_profile_to_a_user_seen_for_the_first_time()
    {
        await TrackAsync(null, "a1", T0);

        var outcome = await Pipeline().HandleAsync(Identify("u1", "a1", "{}", T0.AddMinutes(1)), CancellationToken.None);

        _store.Profiles.Should().ContainSingle();
        _store.ProfileOf(IdentityKey.ExternalId("u1")).Id.Should().Be(outcome.ProfileId);
        outcome.Merged.Should().BeFalse();
    }

    [Fact]
    public async Task E6_email_and_phone_traits_are_promoted_to_columns_and_identities()
    {
        await Pipeline().HandleAsync(Identify("u1", null, """{"email":" Alice@X.io ","phone":"+62 812-3456-789"}""", T0), CancellationToken.None);

        var profile = _store.ProfileOf(IdentityKey.ExternalId("u1"));
        profile.Email.Should().Be("alice@x.io");
        profile.Phone.Should().Be("+628123456789");
        _store.ProfileOf(new IdentityKey("email", "alice@x.io")).Should().BeSameAs(profile);
        _store.ProfileOf(new IdentityKey("phone", "+628123456789")).Should().BeSameAs(profile);
        profile.Attributes.Should().Contain("Alice@X.io", "traits are stored as sent; only the column is normalized");
    }

    [Fact]
    public async Task E6_an_email_owned_by_another_profile_is_not_promoted_and_nothing_is_merged()
    {
        await Pipeline().HandleAsync(Identify("u1", null, """{"email":"shared@x.io"}""", T0), CancellationToken.None);

        var outcome = await Pipeline().HandleAsync(Identify("u2", null, """{"email":"shared@x.io"}""", T0.AddMinutes(1)), CancellationToken.None);

        _store.Profiles.Should().HaveCount(2);
        _store.Profiles[outcome.ProfileId].Email.Should().BeNull();
        _store.ProfileOf(new IdentityKey("email", "shared@x.io")).ExternalId.Should().Be("u1");
        outcome.Merged.Should().BeFalse();
    }
}
