using Crm.Kernel.Paging;
using FluentAssertions;
using Xunit;

namespace Crm.Kernel.UnitTests.Paging;

public sealed class KeysetCursorTests
{
    private static readonly DateTime At = new(2026, 9, 21, 8, 30, 15, 123, DateTimeKind.Utc);
    private static readonly Guid Id = Guid.Parse("01a0c18d-2cd6-7d61-941f-e4133dcc6417");

    [Fact]
    public void Round_trips_timestamp_and_id_as_utc()
    {
        var text = new KeysetCursor(At, Id).Encode("events");

        KeysetCursor.TryDecode("events", text, out var cursor).Should().BeTrue();
        cursor.Timestamp.Should().Be(At);
        cursor.Timestamp.Kind.Should().Be(DateTimeKind.Utc);
        cursor.Id.Should().Be(Id);
    }

    [Fact]
    public void Encoded_text_is_url_safe()
        => new KeysetCursor(At, Id).Encode("profiles").Should().MatchRegex("^[A-Za-z0-9_-]+$");

    [Fact]
    public void Unspecified_kind_round_trips_by_ticks()
    {
        var unspecified = DateTime.SpecifyKind(At, DateTimeKind.Unspecified);
        KeysetCursor.TryDecode("events", new KeysetCursor(unspecified, Id).Encode("events"), out var cursor).Should().BeTrue();
        cursor.Timestamp.Ticks.Should().Be(At.Ticks);
    }

    [Fact]
    public void Wrong_scope_is_rejected()
        => KeysetCursor.TryDecode("profiles", new KeysetCursor(At, Id).Encode("events"), out _).Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 !!")]
    [InlineData("aGVsbG8")]                          // "hello": no separators
    [InlineData("MnxldmVudHN8MXwwMA")]                // version 2
    [InlineData("MXxldmVudHN8YWJjfDAw")]              // ticks not a number
    [InlineData("MXxldmVudHN8LTV8MDFhMGMxOGQyY2Q2N2Q2MTk0MWZlNDEzM2RjYzY0MTc")] // negative ticks
    public void Malformed_text_is_rejected(string? text)
        => KeysetCursor.TryDecode("events", text, out _).Should().BeFalse();

    [Fact]
    public void Oversized_text_is_rejected_without_decoding()
        => KeysetCursor.TryDecode("events", new string('A', 257), out _).Should().BeFalse();
}
