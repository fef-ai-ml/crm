using Crm.Profiles.Application.Queries;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests.Queries;

public sealed class ProfileSearchTermTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_q_means_browse_everything(string? q)
        => ProfileSearchTerm.Parse(q).Value.IsEmpty.Should().BeTrue();

    [Fact]
    public void Text_is_trimmed_and_becomes_a_prefix_pattern()
    {
        var term = ProfileSearchTerm.Parse("  ali ").Value;

        term.IsEmpty.Should().BeFalse();
        term.TextPattern.Should().Be("ali%");
        term.PhonePattern.Should().BeNull();
    }

    [Theory]
    [InlineData("50%_off", @"50\%\_off%")]
    [InlineData(@"a\b", @"a\\b%")]
    [InlineData("x[1]", @"x\[1]%")]
    public void Like_metacharacters_are_escaped(string q, string expected)
        => ProfileSearchTerm.Parse(q).Value.TextPattern.Should().Be(expected);

    [Theory]
    [InlineData("+62812", "+62812%")]
    [InlineData("62812", "+62812%")]              // '+' lost to query-string decoding (ruling F6)
    [InlineData("+62 812-345 (6)", "+628123456%")]
    [InlineData("+6.2", "+62%")]
    public void Phone_like_text_also_gets_a_phone_pattern(string q, string expected)
        => ProfileSearchTerm.Parse(q).Value.PhonePattern.Should().Be(expected);

    [Theory]
    [InlineData("ALI-9")]
    [InlineData("+62a")]
    [InlineData("++62")]
    [InlineData("1234567890123456")]              // 16 digits: not E.164
    [InlineData("-")]
    public void Other_text_has_no_phone_pattern(string q)
        => ProfileSearchTerm.Parse(q).Value.PhonePattern.Should().BeNull();

    [Fact]
    public void Text_longer_than_256_characters_is_a_validation_error()
    {
        var result = ProfileSearchTerm.Parse(new string('a', 257));

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("profiles.invalid_query");
        result.Error.Fields.Should().ContainKey("q");
    }

    [Fact]
    public void Text_of_exactly_256_characters_is_accepted()
        => ProfileSearchTerm.Parse(new string('a', 256)).IsSuccess.Should().BeTrue();
}
