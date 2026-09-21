namespace Crm.Profiles.Application.Seeding;

/// <summary>SplitMix64: fifteen lines we own, so the dataset never depends on System.Random's algorithm (fixtures.md: seed 20260915).</summary>
public sealed class SeedPrng(ulong seed)
{
    private ulong _state = seed;

    public ulong NextUInt64()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>Modulo bias is irrelevant here: the generator's exact counts come from quotas, not from this.</summary>
    public int NextInt(int exclusiveMax)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveMax);
        return (int)(NextUInt64() % (ulong)exclusiveMax);
    }

    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);
}
