namespace Crm.Profiles.Application.Seeding;

/// <summary>
/// The demo dataset of docs/test/fixtures.md. Quota-based: a fixed cell table decides plan, country and dormancy, so the
/// six documented segment counts are exact; the PRNG only shuffles cells over indexes and spreads the filler events
/// (ruling F13). Pure: no clock, no I/O.
/// </summary>
public static class SeedDatasetGenerator
{
    private const int Hour = 3600;
    private const int Day = 86_400;

    private static readonly string[] FirstNames =
    [
        "alice", "bob", "carol", "dave", "erin", "frank", "grace", "heidi", "ivan", "judy",
        "karl", "lena", "mallory", "nina", "oscar", "peggy", "quinn", "rupert", "sybil", "trent",
        "uma", "victor", "wendy", "xavier", "yara", "zane", "adi", "budi", "citra", "dewi",
        "eka", "fajar", "gita", "hadi", "indah", "joko", "kartika", "lukman", "maya", "nanda",
        "omar", "putri", "rani", "sari", "tono", "utami", "vina", "wawan", "yuli", "zahra",
    ];

    private static readonly string[] Cities = ["Jakarta", "Surabaya", "Bandung", "Medan", "Singapore", "Kuala Lumpur", "Penang", "Bangkok", "Chiang Mai", "Denpasar"];

    private static readonly string?[] Countries = ["ID", "SG", "MY", "TH", null];

    /// <summary>Rows: pro, free, no plan. Columns follow <see cref="Countries"/>. Each cell: (active, dormant).</summary>
    private static readonly (string? Plan, (int Active, int Dormant)[] Cells)[] CellTable =
    [
        ("pro", [(200, 40), (100, 22), (250, 20), (170, 15), (168, 15)]),
        ("free", [(1008, 552), (648, 355), (262, 143), (171, 94), (173, 94)]),
        (null, [(140, 60), (88, 37), (52, 23), (35, 15), (35, 15)]),
    ];

    /// <summary>The pinned profile's extra events (ruling F14), taken out of the name quotas below.</summary>
    private static readonly (string Name, int Count)[] PinnedExtras = [("app_open", 72), ("purchase", 24), ("add_to_cart", 18), ("logout", 6)];

    private static readonly (string Name, int Quota)[] NameQuotas = [("app_open", 60_000), ("purchase", 20_000), ("add_to_cart", 15_000), ("logout", 5_000)];

    private const int Purchasers = 2731;
    private const int HighLtvSg = 187;
    private const int LowLtvSg = 813;

    public static SeedData Generate()
    {
        var prng = new SeedPrng(SeedExpectations.PrngSeed);
        var profiles = BuildProfiles(prng);
        var events = BuildEvents(prng, profiles);
        var merges = Enumerable.Range(1, SeedExpectations.MergedProfiles)
            .Select(n => new SeedMerge(n, $"anon-seed-{n:D3}", n * 100))
            .ToList();
        return new SeedData(profiles, events, merges);
    }

    private static List<SeedProfile> BuildProfiles(SeedPrng prng)
    {
        var labels = new List<(string? Plan, string? Country, bool Dormant)>(SeedExpectations.Profiles);
        foreach (var (plan, cells) in CellTable)
        {
            for (var c = 0; c < cells.Length; c++)
            {
                labels.AddRange(Enumerable.Repeat((plan, Countries[c], false), cells[c].Active));
                labels.AddRange(Enumerable.Repeat((plan, Countries[c], true), cells[c].Dormant));
            }
        }

        for (var i = labels.Count - 1; i > 0; i--)
        {
            var j = prng.NextInt(i + 1);
            (labels[i], labels[j]) = (labels[j], labels[i]);
        }

        // Ruling F14: index 1 (alice.1@example.test) is an active pro profile in ID.
        var pin = labels.FindIndex(l => l is ("pro", "ID", false));
        (labels[0], labels[pin]) = (labels[pin], labels[0]);

        var profiles = new List<SeedProfile>(labels.Count);
        int dormantSeen = 0, withEventsSeen = 0, sgSeen = 0, otherSeen = 0;
        for (var index = 1; index <= labels.Count; index++)
        {
            var (plan, country, dormant) = labels[index - 1];
            var hasEvents = !dormant || dormantSeen++ % 6 != 0;
            var purchaser = hasEvents && withEventsSeen++ < Purchasers;

            decimal? ltv;
            if (country == "SG")
            {
                ltv = sgSeen < HighLtvSg ? 150m + Money(850 * Cube(prng.NextDouble()))
                    : sgSeen < HighLtvSg + LowLtvSg ? 1m + Money(148.98 * Square(prng.NextDouble()))
                    : null;
                sgSeen++;
            }
            else
            {
                ltv = otherSeen++ % 5 == 4 ? null : 1m + Money(999 * Cube(prng.NextDouble()));
            }

            profiles.Add(new SeedProfile(
                index,
                $"u-{index:D5}",
                $"{FirstNames[(index - 1) % FirstNames.Length]}.{index}@example.test",
                index % 5 < 3 ? $"+62812{1_000_000 + index:D7}" : null,
                plan,
                country,
                index % 2 == 0 ? Cities[index / 2 % Cities.Length] : null,
                (index % 10) switch { < 3 => true, < 6 => false, _ => null },
                ltv,
                dormant,
                hasEvents,
                purchaser));
        }

        return profiles;
    }

    private static List<SeedEvent> BuildEvents(SeedPrng prng, List<SeedProfile> profiles)
    {
        var events = new List<SeedEvent>(SeedExpectations.Events);
        var remaining = NameQuotas.ToDictionary(q => q.Name, q => q.Quota, StringComparer.Ordinal);
        var withEvents = profiles.Where(p => p.HasEvents).ToList();

        void Add(SeedProfile profile, string name, int minAgeSeconds, int maxAgeSeconds)
        {
            var age = TimeSpan.FromSeconds(minAgeSeconds + prng.NextInt(maxAgeSeconds - minAgeSeconds)) + TimeSpan.FromMilliseconds(prng.NextInt(1000));
            decimal? amount = name == "purchase" ? 5m + Money(195 * prng.NextDouble()) : null;
            events.Add(new SeedEvent(profile.Index, name, age, amount));
            remaining[name]--;
        }

        // Filler events: anywhere in the profile's window, except that a non-purchaser's purchases are older than 35 days.
        void AddFiller(SeedProfile profile, string name)
        {
            var youngest = profile.Dormant ? 8 * Day : Hour;
            if (name == "purchase" && !profile.Purchaser)
            {
                youngest = 35 * Day;
            }

            Add(profile, name, youngest, 89 * Day);
        }

        foreach (var profile in withEvents)
        {
            // Active: one app_open younger than 3 days. Dormant with events: one app_open older than 8 days, so "no events at all" stays at exactly 250.
            Add(profile, "app_open", profile.Dormant ? 8 * Day : Hour, profile.Dormant ? 89 * Day : 3 * Day);
            if (profile.Purchaser)
            {
                Add(profile, "purchase", profile.Dormant ? 8 * Day : Hour, 25 * Day);
            }
        }

        var pinned = profiles[0];
        foreach (var (name, count) in PinnedExtras)
        {
            for (var i = 0; i < count; i++)
            {
                AddFiller(pinned, name);
            }
        }

        foreach (var (name, _) in NameQuotas)
        {
            while (remaining[name] > 0)
            {
                AddFiller(withEvents[prng.NextInt(withEvents.Count)], name);
            }
        }

        return events;
    }

    private static double Square(double u) => u * u;

    private static double Cube(double u) => u * u * u;

    /// <summary>Two decimals, computed from IEEE multiplications only (no transcendental functions), so every platform agrees.</summary>
    private static decimal Money(double value) => Math.Round((decimal)value, 2, MidpointRounding.ToEven);
}
