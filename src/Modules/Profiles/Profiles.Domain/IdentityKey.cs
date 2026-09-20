using System.Text;

namespace Crm.Profiles.Domain;

/// <summary>A normalized (kind, value) pair. Exactly one profile per key per tenant (FR-08).</summary>
public readonly record struct IdentityKey(string Kind, string Value)
{
    private const int MaxEmailLength = 256;   // profiles.Profiles.Email
    private const int MinPhoneDigits = 7;
    private const int MaxPhoneDigits = 15;    // E.164

    /// <summary>Customer ids are opaque: case and whitespace are preserved exactly as the API accepted them.</summary>
    public static IdentityKey ExternalId(string value) => new(IdentityKinds.ExternalId, value);

    public static IdentityKey AnonymousId(string value) => new(IdentityKinds.AnonymousId, value);

    /// <summary>Trimmed and lower-cased; null when the text is not shaped like one address.</summary>
    public static IdentityKey? Email(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim().ToLowerInvariant();
        var at = value.IndexOf('@', StringComparison.Ordinal);
        var isAddress = at > 0 && at < value.Length - 1 && value.IndexOf('@', at + 1) < 0 && !value.Any(char.IsWhiteSpace);
        return isAddress && value.Length <= MaxEmailLength ? new IdentityKey(IdentityKinds.Email, value) : null;
    }

    /// <summary>E.164: '+', a non-zero digit, 7 to 15 digits in total. Spaces, dashes, dots and parentheses are dropped; anything else disqualifies.</summary>
    public static IdentityKey? Phone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed[0] != '+')
        {
            return null;
        }

        var digits = new StringBuilder(trimmed.Length);
        foreach (var c in trimmed.AsSpan(1))
        {
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
            }
            else if (c is not (' ' or '-' or '.' or '(' or ')'))
            {
                return null;
            }
        }

        var valid = digits.Length is >= MinPhoneDigits and <= MaxPhoneDigits && digits[0] != '0';
        return valid ? new IdentityKey(IdentityKinds.Phone, "+" + digits) : null;
    }
}
