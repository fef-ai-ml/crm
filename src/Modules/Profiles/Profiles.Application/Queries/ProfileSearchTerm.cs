using System.Text;
using Crm.Kernel;

namespace Crm.Profiles.Application.Queries;

/// <summary>
/// `q` of GET /v1/profiles, ready for SQL LIKE (ruling F6): a prefix over ExternalId and Email, and — when the text
/// is shaped like the start of an E.164 number — over Phone. IdentityKey.Phone/Email cannot be reused: they reject
/// prefixes. Case-insensitivity is the column collation's job.
/// </summary>
public sealed class ProfileSearchTerm
{
    public const int MaxLength = 256;
    public const string LikeEscape = "\\";

    private static readonly ProfileSearchTerm Empty = new(string.Empty, null);

    private ProfileSearchTerm(string textPattern, string? phonePattern)
    {
        TextPattern = textPattern;
        PhonePattern = phonePattern;
    }

    public bool IsEmpty => TextPattern.Length == 0;

    public string TextPattern { get; }

    public string? PhonePattern { get; }

    public static Result<ProfileSearchTerm> Parse(string? q)
    {
        var text = q?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return Empty;
        }

        if (text.Length > MaxLength)
        {
            return ProfilesErrors.InvalidQuery($"must be at most {MaxLength} characters");
        }

        return new ProfileSearchTerm(Escape(text) + "%", PhonePrefix(text));
    }

    private static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (c is '\\' or '%' or '_' or '[')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>"+62 812-3" → "+628123%"; null when anything but digits, one leading '+' and the separators " -.()" is present.</summary>
    private static string? PhonePrefix(string text)
    {
        var digits = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is >= '0' and <= '9')
            {
                digits.Append(c);
            }
            else if (c == '+' && i == 0)
            {
                // the one allowed '+'
            }
            else if (c is not (' ' or '-' or '.' or '(' or ')'))
            {
                return null;
            }
        }

        return digits.Length is >= 1 and <= 15 ? $"+{digits}%" : null;
    }
}
