using Crm.Tenancy.Infrastructure.Auth;
using FluentAssertions;
using Xunit;

namespace Crm.Tenancy.UnitTests;

public class ApiKeyCacheEntryTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid KeyId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly byte[] KeyHash = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];

    [Fact]
    public void Format_and_TryParse_round_trip()
    {
        var formatted = ApiKeyCacheEntry.Format(TenantId, KeyId, KeyHash);

        ApiKeyCacheEntry.TryParse(formatted, out var tenantId, out var keyId, out var keyHash).Should().BeTrue();
        tenantId.Should().Be(TenantId);
        keyId.Should().Be(KeyId);
        keyHash.Should().Equal(KeyHash);
    }

    [Fact]
    public void TryParse_accepts_lowercase_hex()
    {
        var formatted = ApiKeyCacheEntry.Format(TenantId, KeyId, KeyHash).ToLowerInvariant();

        ApiKeyCacheEntry.TryParse(formatted, out var tenantId, out var keyId, out var keyHash).Should().BeTrue();
        tenantId.Should().Be(TenantId);
        keyId.Should().Be(KeyId);
        keyHash.Should().Equal(KeyHash);
    }

    public static TheoryData<string?> MalformedValues()
    {
        var hex = Convert.ToHexString(KeyHash);
        return new TheoryData<string?>
        {
            null,
            string.Empty,
            $"{TenantId:N}:{KeyId:N}", // too few parts
            $"{TenantId:N}:{KeyId:N}:{hex}:extra", // too many parts
            $"not-a-guid:{KeyId:N}:{hex}", // tenant is not a guid at all
            $"{TenantId:N}:not-a-guid:{hex}", // key is not a guid at all
            $"{TenantId}:{KeyId:N}:{hex}", // tenant guid has dashes, not the "N" format
            $"{TenantId:N}:{KeyId}:{hex}", // key guid has dashes, not the "N" format
            $"{Guid.Empty:N}:{KeyId:N}:{hex}", // Guid.Empty tenant
            $"{TenantId:N}:{Guid.Empty:N}:{hex}", // Guid.Empty key
            $"{TenantId:N}:{KeyId:N}:{hex[..^2]}", // hex one byte short of 32 bytes
            $"{TenantId:N}:{KeyId:N}:{hex}00", // hex one byte longer than 32 bytes
            $"{TenantId:N}:{KeyId:N}:{new string('g', hex.Length)}", // right length, not valid hex characters
        };
    }

    [Theory]
    [MemberData(nameof(MalformedValues))]
    public void TryParse_rejects_malformed_values_without_throwing(string? value)
    {
        ApiKeyCacheEntry.TryParse(value, out var tenantId, out var keyId, out var keyHash).Should().BeFalse();
        tenantId.Should().Be(Guid.Empty);
        keyId.Should().Be(Guid.Empty);
        keyHash.Should().BeEmpty();
    }
}
