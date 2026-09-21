using System.Collections.ObjectModel;

namespace Crm.Profiles.Application.Seeding;

/// <summary>docs/test/fixtures.md in code: the loader verifies these after loading (ruling F15).</summary>
public static class SeedExpectations
{
    public const ulong PrngSeed = 20260915;
    public const int Profiles = 5000;
    public const int MergedProfiles = 50;
    public const int Events = 100_000;
    public const string PinnedExternalId = "u-00001";

    public static IReadOnlyDictionary<string, int> Segments { get; } = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["seed-pro-id"] = 412,
        ["seed-purchasers"] = 2731,
        ["seed-dormant"] = 1500,
        ["seed-no-plan"] = 500,
        ["seed-not-pro"] = 4000,
        ["seed-high-ltv-sg"] = 187,
    });
}
