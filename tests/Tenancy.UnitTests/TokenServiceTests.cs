using System.Text;
using Crm.Kernel.Ids;
using Crm.Kernel.Time;
using Crm.Tenancy.Application;
using Crm.Tenancy.Domain;
using Crm.Tenancy.Infrastructure.Tokens;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Xunit;

namespace Crm.Tenancy.UnitTests;

public class TokenServiceTests
{
    private static readonly DateTime T0 = new(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TenantId = Guid.Parse("0192f0a0-1111-7000-8000-000000000001");
    private const string UserId = "0192f0a0-2222-7000-8000-000000000002";

    private static readonly AuthOptions AuthSettings = new() { SigningKey = "unit-test-signing-key-0123456789abcdef0123456789" };

    private static TokenService CreateService()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(T0);
        return new TokenService(Options.Create(AuthSettings), clock, new UuidV7IdGenerator());
    }

    [Fact]
    public void UT053_access_token_carries_sub_tenant_role_and_expires_in_15_minutes()
    {
        var token = CreateService().IssueAccessToken(UserId, TenantId, [Roles.Admin]);

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(token.Value);
        jwt.GetClaim(TenancyClaims.Subject).Value.Should().Be(UserId);
        jwt.GetClaim(TenancyClaims.TenantId).Value.Should().Be(TenantId.ToString());
        jwt.GetClaim(TenancyClaims.Role).Value.Should().Be(Roles.Admin);
        jwt.GetClaim(TenancyClaims.TokenId).Value.Should().NotBeNullOrWhiteSpace();
        jwt.Issuer.Should().Be("crm");
        jwt.Audiences.Should().Equal("crm-api");
        jwt.ValidTo.Should().Be(T0.AddMinutes(15));
        token.ExpiresInSeconds.Should().Be(900);
        token.ExpiresAtUtc.Should().Be(T0.AddMinutes(15));
    }

    [Fact]
    public void UT053_refresh_token_expires_seven_days_after_issue()
    {
        var issued = RefreshToken.Issue(UserId, T0, AuthSettings.RefreshTokenLifetime);

        issued.Entity.ExpiresAt.Should().Be(T0.AddDays(7));
    }

    [Fact]
    public async Task Issued_token_validates_with_the_published_parameters_and_fails_with_another_key()
    {
        var token = CreateService().IssueAccessToken(UserId, TenantId, [Roles.Viewer]);

        var parameters = TokenService.CreateValidationParameters(AuthSettings);
        parameters.ValidateLifetime = false; // T0 is fixed; lifetime is covered by the ValidTo assertion above
        var valid = await new JsonWebTokenHandler().ValidateTokenAsync(token.Value, parameters);
        valid.IsValid.Should().BeTrue();
        valid.ClaimsIdentity.FindFirst(TenancyClaims.Role)!.Value.Should().Be(Roles.Viewer);

        var other = TokenService.CreateValidationParameters(new AuthOptions { SigningKey = "another-signing-key-0123456789abcdef0123456789ab" });
        other.ValidateLifetime = false;
        (await new JsonWebTokenHandler().ValidateTokenAsync(token.Value, other)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task A_token_signed_hs512_with_the_same_key_fails_validation()
    {
        // HS512 needs a key of at least 64 bytes, so this uses a longer key than the other tests; the point
        // is that ValidAlgorithms rejects HS512 even though the same key could otherwise validate it.
        var settings = new AuthOptions { SigningKey = "unit-test-signing-key-long-enough-for-hs512-0123456789abcdef0123456789" };
        var handler = new JsonWebTokenHandler();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = T0,
            NotBefore = T0,
            Expires = T0.AddMinutes(15),
            Claims = new Dictionary<string, object> { [TenancyClaims.Subject] = UserId },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha512),
        };
        var token = handler.CreateToken(descriptor);

        var parameters = TokenService.CreateValidationParameters(settings);
        var result = await handler.ValidateTokenAsync(token, parameters);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Signing_key_shorter_than_32_bytes_is_rejected()
    {
        var options = new AuthOptions { SigningKey = "too-short" };

        options.IsValid(out var reason).Should().BeFalse();
        reason.Should().Contain("32");
    }
}
