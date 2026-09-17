using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Crm.Kernel;

namespace Crm.Tenancy.Domain;

public sealed record IssuedRefreshToken(RefreshToken Entity, string Plaintext);

/// <summary>Opaque rotating refresh token; stored as SHA-256, chained through <see cref="ReplacedByTokenId"/>.</summary>
public sealed class RefreshToken : Entity
{
    public const int SecretBytes = 32;

    private RefreshToken()
    {
    }

    public string UserId { get; private set; } = string.Empty;

    public byte[] TokenHash { get; private set; } = [];

    public DateTime ExpiresAt { get; private set; }

    public DateTime? RevokedAt { get; private set; }

    public Guid? ReplacedByTokenId { get; private set; }

    public static IssuedRefreshToken Issue(string userId, DateTime utcNow, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var plaintext = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes));
        var entity = new RefreshToken
        {
            UserId = userId,
            TokenHash = HashOf(plaintext),
            ExpiresAt = utcNow + lifetime,
        };
        return new IssuedRefreshToken(entity, plaintext);
    }

    public static byte[] HashOf(string plaintext) => SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));

    public bool IsActive(DateTime utcNow) => RevokedAt is null && utcNow < ExpiresAt;

    public void Revoke(DateTime utcNow, Guid? replacedBy = null)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = utcNow;
        ReplacedByTokenId = replacedBy;
    }
}
