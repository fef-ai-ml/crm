namespace Crm.Profiles.Application.Seeding;

public sealed record SeedProfile(
    int Index, string ExternalId, string Email, string? Phone, string? Plan, string? Country, string? City,
    bool? OptedIn, decimal? Ltv, bool Dormant, bool HasEvents, bool Purchaser);

/// <summary>Age = load time minus OccurredAt, so the generator is independent of the clock.</summary>
public sealed record SeedEvent(int ProfileIndex, string Name, TimeSpan Age, decimal? Amount);

/// <summary>One anonymous profile that was identified into <see cref="WinnerIndex"/>: the loader writes the loser row and the re-pointed anonymous identity.</summary>
public sealed record SeedMerge(int Number, string AnonymousId, int WinnerIndex);

public sealed record SeedData(IReadOnlyList<SeedProfile> Profiles, IReadOnlyList<SeedEvent> Events, IReadOnlyList<SeedMerge> Merges);
