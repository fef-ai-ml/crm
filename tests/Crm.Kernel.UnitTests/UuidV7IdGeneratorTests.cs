using Crm.Kernel.Ids;
using FluentAssertions;
using Xunit;

namespace Crm.Kernel.UnitTests;

public class UuidV7IdGeneratorTests
{
    private static readonly DateTime T0 = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Timestamped_ids_are_version_7_and_order_by_timestamp_as_guid_and_as_string()
    {
        var generator = new UuidV7IdGenerator();

        var first = generator.NewId(T0);
        var second = generator.NewId(T0.AddMilliseconds(1));

        first.Version.Should().Be(7);
        second.Version.Should().Be(7);
        second.CompareTo(first).Should().BePositive();
        string.CompareOrdinal(second.ToString(), first.ToString()).Should().BePositive();
    }

    [Fact]
    public void Timestamp_is_embedded_in_the_first_48_bits()
    {
        var id = new UuidV7IdGenerator().NewId(T0);

        var hex = id.ToString("N")[..12];
        Convert.ToInt64(hex, 16).Should().Be(new DateTimeOffset(T0).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void An_unspecified_kind_is_treated_as_utc()
    {
        var unspecified = DateTime.SpecifyKind(T0, DateTimeKind.Unspecified);

        new UuidV7IdGenerator().NewId(unspecified).ToString("N")[..12]
            .Should().Be(new UuidV7IdGenerator().NewId(T0).ToString("N")[..12]);
    }
}
