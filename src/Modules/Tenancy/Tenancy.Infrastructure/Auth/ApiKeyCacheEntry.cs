using System.Globalization;

namespace Crm.Tenancy.Infrastructure.Auth;

/// <summary>
/// The Redis cache entry format for an API key: <c>"{tenantId:N}:{keyId:N}:{keyHashHex}"</c>.
/// <see cref="TryParse"/> never throws, so a corrupt or foreign cache value is treated as a cache miss
/// rather than surfacing as an unhandled exception.
/// </summary>
public static class ApiKeyCacheEntry
{
    private const int KeyHashLength = 32; // SHA-256
    private const int KeyHashHexLength = KeyHashLength * 2;

    public static string Format(Guid tenantId, Guid keyId, byte[] keyHash)
    {
        ArgumentNullException.ThrowIfNull(keyHash);
        return string.Create(CultureInfo.InvariantCulture, $"{tenantId:N}:{keyId:N}:{Convert.ToHexString(keyHash)}");
    }

    public static bool TryParse(string? value, out Guid tenantId, out Guid keyId, out byte[] keyHash)
    {
        tenantId = Guid.Empty;
        keyId = Guid.Empty;
        keyHash = [];

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var parts = value.Split(':');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!Guid.TryParseExact(parts[0], "N", out var parsedTenantId) || parsedTenantId == Guid.Empty)
        {
            return false;
        }

        if (!Guid.TryParseExact(parts[1], "N", out var parsedKeyId) || parsedKeyId == Guid.Empty)
        {
            return false;
        }

        if (parts[2].Length != KeyHashHexLength)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[KeyHashLength];
        if (!TryDecodeHex(parts[2], buffer))
        {
            return false;
        }

        tenantId = parsedTenantId;
        keyId = parsedKeyId;
        keyHash = buffer.ToArray();
        return true;
    }

    /// <summary>
    /// The BCL exposes only throwing hex decoders (<see cref="Convert.FromHexString(string)"/>); this
    /// decodes into a fixed-size buffer without ever throwing, so a corrupt cache value is rejected rather
    /// than surfaced as an unhandled <see cref="FormatException"/>.
    /// </summary>
    private static bool TryDecodeHex(string hex, Span<byte> destination)
    {
        if (hex.Length != destination.Length * 2)
        {
            return false;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            var high = HexValue(hex[i * 2]);
            var low = HexValue(hex[(i * 2) + 1]);
            if (high < 0 || low < 0)
            {
                return false;
            }

            destination[i] = (byte)((high << 4) | low);
        }

        return true;

        static int HexValue(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
    }
}
