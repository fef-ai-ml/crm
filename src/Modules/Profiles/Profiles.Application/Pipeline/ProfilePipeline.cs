using System.Text.Json;
using Crm.Contracts;
using Crm.Profiles.Application.Messages;
using Crm.Profiles.Domain;

namespace Crm.Profiles.Application.Pipeline;

/// <summary>
/// Spec §4.2 for one message: resolve identities (locking every profile row it touches and following MergedIntoId),
/// merge on identify, apply, append — all inside the store's single transaction — and describe what to publish.
/// </summary>
public sealed class ProfilePipeline(IProfileStore store, IPipelineHooks hooks)
{
    private const string EmailTrait = "email";
    private const string PhoneTrait = "phone";

    public async Task<ProcessingOutcome> HandleAsync(RawMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        await store.BeginAsync(cancellationToken);
        var outcome = message switch
        {
            TrackMessage track => await TrackAsync(track.Payload, cancellationToken),
            IdentifyMessage identify => await IdentifyAsync(identify.Payload, cancellationToken),
            _ => throw new PoisonMessageException($"Unsupported message {message.GetType().Name}."),
        };
        await store.CommitAsync(cancellationToken);
        return outcome;
    }

    private async Task<ProcessingOutcome> TrackAsync(TrackEventRaw raw, CancellationToken cancellationToken)
    {
        var resolution = new Resolution();
        var target = await ResolveAsync(raw.TenantId, raw.UserId, raw.AnonymousId, raw.ReceivedAt, allowMerge: false, resolution, cancellationToken);

        var properties = raw.Properties.ValueKind == JsonValueKind.Object ? raw.Properties.GetRawText() : ProfileAttributes.Empty;
        var record = new EventRecord(raw.EventId, raw.TenantId, target.Id, raw.Event, properties, raw.OccurredAt, raw.ReceivedAt);
        var appended = await store.TryAppendEventAsync(record, cancellationToken);
        if (appended)
        {
            target.ApplyTrack(raw.ReceivedAt);
        }

        // Ruling E7: a duplicate is announced again — its first delivery may have died between the SQL commit and the produce.
        return new ProcessingOutcome([ProcessedMessageFactory.EventTracked(record, target)], target.Id, Duplicate: !appended, resolution.Redirected, Merged: false);
    }

    private async Task<ProcessingOutcome> IdentifyAsync(IdentifyRaw raw, CancellationToken cancellationToken)
    {
        var resolution = new Resolution();
        var target = await ResolveAsync(raw.TenantId, raw.UserId, raw.AnonymousId, raw.ReceivedAt, allowMerge: true, resolution, cancellationToken);

        var changed = target.ApplyIdentify(raw.Traits, raw.ReceivedAt);
        await PromoteContactPointsAsync(target, raw.Traits, cancellationToken);

        var messages = new List<ProcessedMessage>();
        if (resolution.Merge is { } merge)
        {
            messages.Add(ProcessedMessageFactory.ProfileMerged(raw.TenantId, merge.WinnerId, merge.LoserId, raw.ReceivedAt));
        }

        var changedKeys = (resolution.Merge?.GainedKeys ?? []).Concat(changed).Distinct(StringComparer.Ordinal).ToList();
        if (changedKeys.Count > 0 || resolution.Created || resolution.Merge is not null)
        {
            messages.Add(ProcessedMessageFactory.ProfileUpdated(target, changedKeys, raw.ReceivedAt));
        }

        return new ProcessingOutcome(messages, target.Id, Duplicate: false, resolution.Redirected, Merged: resolution.Merge is not null);
    }

    /// <summary>Rulings E5: who the message is about, creating or linking what is missing.</summary>
    private async Task<Profile> ResolveAsync(Guid tenantId, string? userId, string? anonymousId, DateTime receivedAt, bool allowMerge, Resolution resolution, CancellationToken cancellationToken)
    {
        IdentityKey? userKey = string.IsNullOrWhiteSpace(userId) ? null : IdentityKey.ExternalId(userId);
        IdentityKey? anonymousKey = string.IsNullOrWhiteSpace(anonymousId) ? null : IdentityKey.AnonymousId(anonymousId);

        var userProfileId = userKey is { } uk ? await store.FindProfileIdAsync(uk, bypassCache: false, cancellationToken) : null;
        var anonymousProfileId = anonymousKey is { } ak ? await store.FindProfileIdAsync(ak, bypassCache: false, cancellationToken) : null;

        var (known, anonymous) = await LockBothAsync(userKey, userProfileId, anonymousKey, anonymousProfileId, resolution, cancellationToken);

        if (known is not null && anonymous is not null && known.Id != anonymous.Id)
        {
            // Both rows are locked here. A profile that already has an external id is another known user
            // (shared device): never merged, and track never merges at all.
            return allowMerge && anonymous.ExternalId is null
                ? await MergeAsync(known, anonymous, resolution, cancellationToken)
                : known;
        }

        if (known is not null)
        {
            if (anonymousKey is { } unmapped && anonymous is null)
            {
                Attach(tenantId, unmapped, known);
            }

            return known;
        }

        if (anonymous is not null && (userKey is null || anonymous.ExternalId is null))
        {
            if (userKey is { } promoteTo)
            {
                anonymous.AssignExternalId(promoteTo.Value);
                Attach(tenantId, promoteTo, anonymous);
            }

            return anonymous;
        }

        // Nobody known yet — or the anonymous id belongs to a different known user, in which case this user starts fresh.
        var created = Profile.Create(tenantId, receivedAt);
        store.Add(created);
        resolution.Created = true;
        if (userKey is { } newUser)
        {
            created.AssignExternalId(newUser.Value);
            Attach(tenantId, newUser, created);
        }

        if (anonymousKey is { } newAnonymous && anonymous is null)
        {
            Attach(tenantId, newAnonymous, created);
        }

        return created;
    }

    /// <summary>
    /// I2: when both keys already resolved to an id, lock the two rows in ascending profile-id order rather than
    /// role order, so two identifies that name the same pair of profiles with roles swapped cannot deadlock (SQL
    /// error 1205). Following a MergedIntoId chain inside <see cref="LockFoundAsync"/> can still land the real lock
    /// out of that order — accepted: it is rare, and SQL Server's deadlock victim is retried by the message retry
    /// policy.
    /// </summary>
    private async Task<(Profile? Known, Profile? Anonymous)> LockBothAsync(
        IdentityKey? userKey, Guid? userProfileId, IdentityKey? anonymousKey, Guid? anonymousProfileId,
        Resolution resolution, CancellationToken cancellationToken)
    {
        if (userKey is { } uk && userProfileId is { } uid && anonymousKey is { } ak && anonymousProfileId is { } aid)
        {
            if (uid == aid)
            {
                var profile = await LockFoundAsync(uk, uid, resolution, cancellationToken);
                return (profile, profile);
            }

            if (uid.CompareTo(aid) < 0)
            {
                var known = await LockFoundAsync(uk, uid, resolution, cancellationToken);
                var anonymous = await LockFoundAsync(ak, aid, resolution, cancellationToken);
                return (known, anonymous);
            }

            var lockedAnonymous = await LockFoundAsync(ak, aid, resolution, cancellationToken);
            var lockedKnown = await LockFoundAsync(uk, uid, resolution, cancellationToken);
            return (lockedKnown, lockedAnonymous);
        }

        var soleKnown = userKey is { } onlyUk && userProfileId is { } onlyUid
            ? await LockFoundAsync(onlyUk, onlyUid, resolution, cancellationToken)
            : null;
        var soleAnonymous = anonymousKey is { } onlyAk && anonymousProfileId is { } onlyAid
            ? await LockFoundAsync(onlyAk, onlyAid, resolution, cancellationToken)
            : null;
        return (soleKnown, soleAnonymous);
    }

    /// <summary>Locks a profile id already found for <paramref name="key"/>, with the stale-cache fallback and the
    /// post-lock MergedIntoId redirect.</summary>
    private async Task<Profile?> LockFoundAsync(IdentityKey key, Guid profileId, Resolution resolution, CancellationToken cancellationToken)
    {
        var profile = await store.LockProfileAsync(profileId, cancellationToken);
        if (profile is null)
        {
            // The cache pointed at a row that does not exist (database rebuilt under a warm Redis).
            store.Forget(key);
            var refoundId = await store.FindProfileIdAsync(key, bypassCache: true, cancellationToken);
            if (refoundId is null)
            {
                return null;
            }

            profile = await store.LockProfileAsync(refoundId.Value, cancellationToken)
                ?? throw new InvalidOperationException($"Identity {key.Kind}:{key.Value} points to missing profile {refoundId}.");
        }

        if (profile.MergedIntoId is null)
        {
            return profile;
        }

        // The id came from before a merge committed. Apply to the winner and repair the cache.
        var winner = await WinnerResolver.ResolveAsync(profile, store.LockProfileAsync, cancellationToken);
        resolution.Redirected = true;
        store.Forget(key);
        store.Remember(key, winner.Id);
        return winner;
    }

    private async Task<Profile> MergeAsync(Profile first, Profile second, Resolution resolution, CancellationToken cancellationToken)
    {
        await hooks.MergeLocksTakenAsync(first.TenantId, cancellationToken);

        var (winner, loser) = MergePolicy.Decide(first, second);
        var loserIdentities = await store.GetIdentitiesAsync(loser.Id, cancellationToken);
        resolution.Merge = ProfileMerge.Apply(winner, loser, loserIdentities);

        foreach (var identity in loserIdentities)
        {
            store.Forget(identity.Key);
        }

        await store.RepointEventsAsync(loser.Id, winner.Id, cancellationToken);
        winner.SetEventCount(await store.CountEventsAsync(winner.Id, cancellationToken));
        return winner;
    }

    /// <summary>Ruling E6: email/phone become columns and identities only when nobody else owns them.</summary>
    private async Task PromoteContactPointsAsync(Profile target, JsonElement traits, CancellationToken cancellationToken)
    {
        if (traits.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (StringTrait(traits, EmailTrait) is { } email && IdentityKey.Email(email) is { } emailKey
            && await ClaimAsync(target, emailKey, cancellationToken))
        {
            target.AssignEmail(emailKey.Value);
        }

        if (StringTrait(traits, PhoneTrait) is { } phone && IdentityKey.Phone(phone) is { } phoneKey
            && await ClaimAsync(target, phoneKey, cancellationToken))
        {
            target.AssignPhone(phoneKey.Value);
        }
    }

    private async Task<bool> ClaimAsync(Profile target, IdentityKey key, CancellationToken cancellationToken)
    {
        var owner = await store.FindProfileIdAsync(key, bypassCache: true, cancellationToken);
        if (owner is null)
        {
            Attach(target.TenantId, key, target);
            return true;
        }

        return owner == target.Id;
    }

    private void Attach(Guid tenantId, IdentityKey key, Profile profile)
    {
        store.Add(new UserIdentity(tenantId, key, profile.Id));
        store.Remember(key, profile.Id);
    }

    private static string? StringTrait(JsonElement traits, string name)
        => traits.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed class Resolution
    {
        public bool Created { get; set; }

        public bool Redirected { get; set; }

        public ProfileMergedEvent? Merge { get; set; }
    }
}
