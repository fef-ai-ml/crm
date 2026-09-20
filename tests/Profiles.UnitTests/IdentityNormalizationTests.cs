using Crm.Profiles.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class IdentityNormalizationTests
{
    [Fact]
    public void UT009_identity_values_are_normalized()
    {
        IdentityKey.Email(" A@B.com ").Should().Be(new IdentityKey("email", "a@b.com"));
        IdentityKey.Phone("+62 812-3456").Should().Be(new IdentityKey("phone", "+628123456"));
        IdentityKey.ExternalId("U-1").Should().Be(new IdentityKey("external_id", "U-1"));
        IdentityKey.AnonymousId("Anon-9").Should().Be(new IdentityKey("anonymous_id", "Anon-9"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("@b.com")]
    [InlineData("a@")]
    [InlineData("a@b@c.com")]
    public void UT009_strings_that_are_not_an_email_yield_no_identity(string? raw)
        => IdentityKey.Email(raw).Should().BeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("08123456789")]      // no country code
    [InlineData("+0123456789")]      // country codes never start with 0
    [InlineData("+62")]              // too short
    [InlineData("+6281234567890123456")] // longer than E.164 allows
    [InlineData("+62 81a 234")]
    public void UT009_strings_that_are_not_e164_yield_no_identity(string? raw)
        => IdentityKey.Phone(raw).Should().BeNull();

    [Fact]
    public void UT009_phone_punctuation_is_removed()
        => IdentityKey.Phone("+1 (415) 555.0101").Should().Be(new IdentityKey("phone", "+14155550101"));

    [Fact]
    public void UT009_email_longer_than_the_column_is_rejected()
        => IdentityKey.Email(new string('a', 251) + "@b.com").Should().BeNull();

    [Fact]
    public void UT009_email_exactly_as_long_as_the_column_is_accepted()
    {
        var key = IdentityKey.Email(new string('a', 250) + "@b.com");

        key.Should().NotBeNull();
        key!.Value.Value.Should().HaveLength(256);
    }
}
