using System.Text.Json;
using Crm.Kernel;

namespace Crm.Profiles.Domain;

/// <summary>One unified end user of a tenant (profiles.Profiles). Mutated only while its row is locked.</summary>
public sealed class Profile : Entity
{
    private Profile()
    {
    }

    public string? ExternalId { get; private set; }

    public string? Email { get; private set; }

    public string? Phone { get; private set; }

    public string Attributes { get; private set; } = ProfileAttributes.Empty;

    public DateTime FirstSeenAt { get; private set; }

    public DateTime LastSeenAt { get; private set; }

    public long EventCount { get; private set; }

    /// <summary>Non-null on a merge loser; such rows are excluded from every read and only redirect to the winner.</summary>
    public Guid? MergedIntoId { get; private set; }

    public static Profile Create(Guid tenantId, DateTime seenAt)
    {
        var at = EventTime.TruncateToMilliseconds(seenAt);
        return new Profile { TenantId = tenantId, FirstSeenAt = at, LastSeenAt = at };
    }

    public void ApplyTrack(DateTime receivedAt)
    {
        EventCount++;
        Touch(receivedAt);
    }

    /// <summary>identify never changes EventCount (FR-10). Returns the keys whose value changed.</summary>
    public IReadOnlyList<string> ApplyIdentify(JsonElement traits, DateTime receivedAt)
    {
        var change = ProfileAttributes.ApplyTraits(Attributes, traits);
        Attributes = change.Json;
        Touch(receivedAt);
        return change.ChangedKeys;
    }

    public void AssignExternalId(string externalId)
    {
        if (ExternalId is not null && !string.Equals(ExternalId, externalId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Profile {Id} already belongs to external id '{ExternalId}'.");
        }

        ExternalId = externalId;
    }

    public void AssignEmail(string normalizedEmail) => Email = normalizedEmail;

    public void AssignPhone(string normalizedPhone) => Phone = normalizedPhone;

    public void SetEventCount(long count) => EventCount = count;

    internal IReadOnlyList<string> Absorb(Profile loser)
    {
        var change = ProfileAttributes.MergeLoser(Attributes, loser.Attributes);
        Attributes = change.Json;
        ExternalId ??= loser.ExternalId;
        Email ??= loser.Email;
        Phone ??= loser.Phone;
        FirstSeenAt = loser.FirstSeenAt < FirstSeenAt ? loser.FirstSeenAt : FirstSeenAt;
        LastSeenAt = loser.LastSeenAt > LastSeenAt ? loser.LastSeenAt : LastSeenAt;
        return change.ChangedKeys;
    }

    internal void MarkMergedInto(Guid winnerId)
    {
        ExternalId = null;
        Email = null;
        Phone = null;
        MergedIntoId = winnerId;
    }

    private void Touch(DateTime receivedAt)
    {
        var at = EventTime.TruncateToMilliseconds(receivedAt);
        if (at > LastSeenAt)
        {
            LastSeenAt = at;
        }
    }
}
