using Crm.Profiles.Application.Seeding;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests.Seeding;

public sealed class SeedPrngTests
{
    [Fact]
    public void SplitMix64_reference_vector_for_seed_1234567()
    {
        var prng = new SeedPrng(1234567);
        new[] { prng.NextUInt64(), prng.NextUInt64(), prng.NextUInt64() }
            .Should().Equal(6457827717110365317UL, 3203168211198807973UL, 9817491932198370423UL);
    }

    [Fact]
    public void NextInt_stays_below_the_bound_and_NextDouble_inside_the_unit_interval()
    {
        var prng = new SeedPrng(1);
        for (var i = 0; i < 10_000; i++)
        {
            prng.NextInt(7).Should().BeInRange(0, 6);
            prng.NextDouble().Should().BeInRange(0d, 1d).And.BeLessThan(1d);
        }
    }
}
