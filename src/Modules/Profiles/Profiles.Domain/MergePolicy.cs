namespace Crm.Profiles.Domain;

public static class MergePolicy
{
    /// <summary>FR-09: the older profile wins. Equal FirstSeenAt falls back to the smaller id so both argument orders agree.</summary>
    public static (Profile Winner, Profile Loser) Decide(Profile a, Profile b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var byAge = a.FirstSeenAt.CompareTo(b.FirstSeenAt);
        var aWins = byAge < 0 || (byAge == 0 && a.Id.CompareTo(b.Id) <= 0);
        return aWins ? (a, b) : (b, a);
    }
}
