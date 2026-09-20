namespace Crm.Profiles.Domain;

public sealed record ProfileMergedEvent(Guid WinnerId, Guid LoserId, IReadOnlyList<string> GainedKeys);

/// <summary>
/// The in-memory half of a merge. The caller holds UPDLOCK, HOLDLOCK on both rows, re-points profiles.Events with
/// one UPDATE and recomputes the winner's EventCount afterwards (spec section 4.2 steps 2-3).
/// </summary>
public static class ProfileMerge
{
    public static ProfileMergedEvent Apply(Profile winner, Profile loser, IEnumerable<UserIdentity> loserIdentities)
    {
        ArgumentNullException.ThrowIfNull(winner);
        ArgumentNullException.ThrowIfNull(loser);
        ArgumentNullException.ThrowIfNull(loserIdentities);

        if (winner.Id == loser.Id)
        {
            throw new InvalidOperationException($"Profile {winner.Id} cannot be merged into itself.");
        }

        if (winner.MergedIntoId is not null || loser.MergedIntoId is not null)
        {
            throw new InvalidOperationException("Both profiles must be live; resolve merge chains before merging.");
        }

        var gained = winner.Absorb(loser);
        foreach (var identity in loserIdentities)
        {
            identity.RepointTo(winner.Id);
        }

        loser.MarkMergedInto(winner.Id);
        return new ProfileMergedEvent(winner.Id, loser.Id, gained);
    }
}
