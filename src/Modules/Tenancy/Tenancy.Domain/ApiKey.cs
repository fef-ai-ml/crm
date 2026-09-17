using System.Security.Cryptography;
using System.Text;
using Crm.Kernel;

namespace Crm.Tenancy.Domain;

public enum ApiKeyVerification
{
    Valid,
    Invalid,
    Revoked,
}

public sealed record GeneratedApiKey(ApiKey Entity, string Plaintext);

/// <summary>Tenant ingest credential. Only the SHA-256 of the key is kept; the plaintext exists once, in <see cref="GeneratedApiKey"/>.</summary>
public sealed class ApiKey : Entity
{
    public const string KeyPrefix = "crm_live_";
    public const int PrefixLength = 8;
    public const int SecretLength = 32;
    public const int NameMaxLength = 128;

    private const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private ApiKey()
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string Prefix { get; private set; } = string.Empty;

    public byte[] KeyHash { get; private set; } = [];

    public string CreatedByUserId { get; private set; } = string.Empty;

    public DateTime? RevokedAt { get; private set; }

    public DateTime? LastUsedAt { get; private set; }

    public static GeneratedApiKey Generate(string name, string createdByUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdByUserId);

        var secret = RandomNumberGenerator.GetString(Base62, SecretLength);
        var plaintext = KeyPrefix + secret;
        var entity = new ApiKey
        {
            Name = name.Trim(),
            Prefix = secret[..PrefixLength],
            KeyHash = Hash(plaintext),
            CreatedByUserId = createdByUserId,
        };
        return new GeneratedApiKey(entity, plaintext);
    }

    public static byte[] Hash(string plaintext) => SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));

    public static bool TryParsePrefix(string? plaintext, out string prefix)
    {
        prefix = string.Empty;
        if (plaintext is null
            || plaintext.Length != KeyPrefix.Length + SecretLength
            || !plaintext.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var secret = plaintext.AsSpan(KeyPrefix.Length);
        foreach (var c in secret)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        prefix = secret[..PrefixLength].ToString();
        return true;
    }

    public ApiKeyVerification Verify(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (!CryptographicOperations.FixedTimeEquals(Hash(plaintext), KeyHash))
        {
            return ApiKeyVerification.Invalid;
        }

        return RevokedAt is null ? ApiKeyVerification.Valid : ApiKeyVerification.Revoked;
    }

    public void Revoke(DateTime utcNow) => RevokedAt ??= utcNow;

    public void Touch(DateTime utcNow) => LastUsedAt = utcNow;
}
