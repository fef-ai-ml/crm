using System.Text.Json;
using Crm.Profiles.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class ApplyTraitsTests
{
    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    [Fact]
    public void UT015_traits_apply_per_key_last_write_wins()
    {
        var change = ProfileAttributes.ApplyTraits("""{"plan":"free","city":"SG"}""", Json("""{"plan":"pro","country":"ID"}"""));

        Json(change.Json).GetProperty("plan").GetString().Should().Be("pro");
        Json(change.Json).GetProperty("city").GetString().Should().Be("SG");
        Json(change.Json).GetProperty("country").GetString().Should().Be("ID");
        change.ChangedKeys.Should().Equal("plan", "country");
    }

    [Fact]
    public void UT015_an_equal_value_is_not_a_change()
    {
        var change = ProfileAttributes.ApplyTraits("""{"plan":"pro","age":30}""", Json("""{"plan":"pro","age":30}"""));

        change.ChangedKeys.Should().BeEmpty();
    }

    [Fact]
    public void UT015_a_null_trait_removes_the_key()
    {
        var change = ProfileAttributes.ApplyTraits("""{"plan":"pro","city":"SG"}""", Json("""{"city":null,"ghost":null}"""));

        Json(change.Json).TryGetProperty("city", out _).Should().BeFalse();
        change.ChangedKeys.Should().Equal("city");
    }

    [Fact]
    public void UT015_nested_values_are_replaced_whole()
    {
        var change = ProfileAttributes.ApplyTraits("""{"prefs":{"a":1,"b":2}}""", Json("""{"prefs":{"a":1}}"""));

        Json(change.Json).GetProperty("prefs").TryGetProperty("b", out _).Should().BeFalse();
        change.ChangedKeys.Should().Equal("prefs");
    }

    [Fact]
    public void UT015_traits_that_are_not_an_object_change_nothing()
    {
        ProfileAttributes.ApplyTraits("""{"plan":"pro"}""", default).ChangedKeys.Should().BeEmpty();
        ProfileAttributes.ApplyTraits("""{"plan":"pro"}""", Json("[1,2]")).Json.Should().Be("""{"plan":"pro"}""");
    }

    [Fact]
    public void UT011_merge_keeps_winner_attributes_on_conflict()
    {
        var change = ProfileAttributes.MergeLoser("""{"plan":"pro","city":"SG"}""", """{"plan":"free","country":"ID"}""");

        Json(change.Json).GetProperty("plan").GetString().Should().Be("pro");
        Json(change.Json).GetProperty("city").GetString().Should().Be("SG");
        Json(change.Json).GetProperty("country").GetString().Should().Be("ID");
        change.ChangedKeys.Should().Equal("country");
    }
}
