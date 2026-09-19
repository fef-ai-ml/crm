using System.Text;
using System.Text.Json;
using Crm.Ingestion.Application.Requests;
using Crm.Ingestion.Application.Validation;
using FluentAssertions;
using Xunit;

namespace Crm.Ingestion.UnitTests;

public class TrackValidatorTests
{
    private static readonly DateTime Now = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);
    private readonly TrackRequestValidator _validator = new(new FakeClock(Now));

    private static TrackRequest Valid(string? userId = "u1", string? anonymousId = null, string? @event = "purchase", JsonElement? properties = null, DateTimeOffset? timestamp = null)
        => new(userId, anonymousId, @event, properties, timestamp);

    private IReadOnlyDictionary<string, string[]> Errors(TrackRequest request) => ValidationFields.From(_validator.Validate(request));

    [Fact]
    public void UT001_track_requires_userId_or_anonymousId()
    {
        Errors(Valid(userId: null, anonymousId: null)).Should().ContainKey("userId")
            .WhoseValue.Should().Equal("userId or anonymousId is required");
        Errors(Valid(userId: "   ", anonymousId: "")).Should().ContainKey("userId");
        Errors(Valid(userId: "u1", anonymousId: null)).Should().BeEmpty();
        Errors(Valid(userId: null, anonymousId: "a1")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("purchase", true)]
    [InlineData("a_1", true)]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", true)]   // 64
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", false)] // 65
    [InlineData("bad name!", false)]
    [InlineData("émoji", false)]
    [InlineData("purchase\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void UT002_event_name_pattern_and_length(string? name, bool valid)
    {
        var errors = Errors(Valid(@event: name));

        if (valid)
        {
            errors.Should().BeEmpty();
        }
        else
        {
            errors.Should().ContainKey("event").WhoseValue.Should().Equal("must match [A-Za-z0-9_]{1,64}");
        }
    }

    [Fact]
    public void UT003_properties_size_limit_is_32_kb_of_utf8()
    {
        Errors(Valid(properties: ObjectOfBytes(32_768))).Should().BeEmpty();
        Errors(Valid(properties: ObjectOfBytes(32_769))).Should().ContainKey("properties")
            .WhoseValue.Should().Equal("must be at most 32 KB");
    }

    [Fact]
    public void Properties_must_be_an_object_when_present()
    {
        Errors(Valid(properties: Json("[1,2]"))).Should().ContainKey("properties")
            .WhoseValue.Should().Equal("must be a JSON object");
        Errors(Valid(properties: Json("null"))).Should().BeEmpty();
        Errors(Valid(properties: Json("{}"))).Should().BeEmpty();
    }

    [Fact]
    public void UT004_timestamp_window_is_seven_days_either_side_inclusive()
    {
        var now = new DateTimeOffset(Now);

        Errors(Valid(timestamp: now.AddDays(-7))).Should().BeEmpty();
        Errors(Valid(timestamp: now.AddDays(7))).Should().BeEmpty();
        Errors(Valid(timestamp: now.AddDays(-7).AddSeconds(-1))).Should().ContainKey("timestamp");
        Errors(Valid(timestamp: now.AddDays(7).AddSeconds(1))).Should().ContainKey("timestamp")
            .WhoseValue.Should().Equal("must be within 7 days of now");
        Errors(Valid(timestamp: null)).Should().BeEmpty();
    }

    [Fact]
    public void Identifier_lengths_are_bounded_by_the_columns_they_end_up_in()
    {
        Errors(Valid(userId: new string('u', 128))).Should().BeEmpty();
        Errors(Valid(userId: new string('u', 129))).Should().ContainKey("userId")
            .WhoseValue.Should().Equal("must be at most 128 characters");
        Errors(Valid(userId: null, anonymousId: new string('a', 257))).Should().ContainKey("anonymousId")
            .WhoseValue.Should().Equal("must be at most 256 characters");
    }

    /// <summary>A JSON object whose UTF-8 text is exactly <paramref name="bytes"/> bytes: {"k":"aaa…"} has 8 bytes of punctuation.</summary>
    private static JsonElement ObjectOfBytes(int bytes)
    {
        var text = "{\"k\":\"" + new string('a', bytes - 8) + "\"}";
        Encoding.UTF8.GetByteCount(text).Should().Be(bytes);
        return Json(text);
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
