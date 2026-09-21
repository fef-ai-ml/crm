using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Crm.Kernel.Paging;

/// <summary>
/// Position in a keyset-ordered list: the sort timestamp and the row id that breaks ties. On the wire it is
/// Base64Url("1|scope|ticks|id") — opaque to clients, not tenant-bound (it only positions a query that is already
/// tenant-filtered), and rejected when the scope is another endpoint's (ruling F2).
/// </summary>
public readonly record struct KeysetCursor(DateTime Timestamp, Guid Id)
{
    private const string Version = "1";
    private const int MaxTextLength = 256;

    public string Encode(string scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        var plain = string.Create(CultureInfo.InvariantCulture, $"{Version}|{scope}|{Timestamp.Ticks}|{Id:N}");
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(plain));
    }

    public static bool TryDecode(string scope, string? text, out KeysetCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
        {
            return false;
        }

        string plain;
        try
        {
            plain = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(text));
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = plain.Split('|');
        if (parts.Length != 4
            || parts[0] != Version
            || !string.Equals(parts[1], scope, StringComparison.Ordinal)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks > DateTime.MaxValue.Ticks
            || !Guid.TryParseExact(parts[3], "N", out var id))
        {
            return false;
        }

        cursor = new KeysetCursor(new DateTime(ticks, DateTimeKind.Utc), id);
        return true;
    }
}
