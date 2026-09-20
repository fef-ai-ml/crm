namespace Crm.Profiles.Domain;

public static class WinnerResolver
{
    public const int MaxHops = 16;

    /// <summary>
    /// Follows MergedIntoId until a live profile is reached. <paramref name="load"/> is expected to lock the row it
    /// returns, so an event that raced a merge is applied to the winner (spec section 4.2, semantics item 2).
    /// </summary>
    public static async Task<Profile> ResolveAsync(Profile start, Func<Guid, CancellationToken, Task<Profile?>> load, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(load);

        var visited = new HashSet<Guid> { start.Id };
        var current = start;
        while (current.MergedIntoId is { } next)
        {
            if (!visited.Add(next) || visited.Count > MaxHops)
            {
                throw new MergeChainCorruptException($"Merge chain starting at profile {start.Id} loops or exceeds {MaxHops} hops.");
            }

            current = await load(next, cancellationToken)
                ?? throw new MergeChainCorruptException($"Profile {current.Id} points to missing profile {next}.");
        }

        return current;
    }
}
