using Crm.Tenancy.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.Tenancy.UnitTests;

public class RefreshTokenTests
{
    private static readonly DateTime T0 = new(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Issue_returns_a_base64url_secret_and_stores_only_its_hash()
    {
        var issued = RefreshToken.Issue("user-1", T0, TimeSpan.FromDays(7));

        issued.Plaintext.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        issued.Entity.TokenHash.Should().Equal(RefreshToken.HashOf(issued.Plaintext));
        issued.Entity.ExpiresAt.Should().Be(T0.AddDays(7));
        issued.Entity.UserId.Should().Be("user-1");
    }

    [Fact]
    public void IsActive_is_false_after_expiry_or_revocation()
    {
        var issued = RefreshToken.Issue("user-1", T0, TimeSpan.FromDays(7));
        issued.Entity.IsActive(T0.AddDays(6)).Should().BeTrue();
        issued.Entity.IsActive(T0.AddDays(7)).Should().BeFalse();

        var replacement = Guid.CreateVersion7();
        issued.Entity.Revoke(T0.AddHours(1), replacement);

        issued.Entity.IsActive(T0.AddHours(2)).Should().BeFalse();
        issued.Entity.RevokedAt.Should().Be(T0.AddHours(1));
        issued.Entity.ReplacedByTokenId.Should().Be(replacement);
    }

    [Fact]
    public void Revoke_keeps_the_first_revocation()
    {
        var issued = RefreshToken.Issue("user-1", T0, TimeSpan.FromDays(7));
        issued.Entity.Revoke(T0.AddHours(1));
        issued.Entity.Revoke(T0.AddHours(2));

        issued.Entity.RevokedAt.Should().Be(T0.AddHours(1));
    }
}
