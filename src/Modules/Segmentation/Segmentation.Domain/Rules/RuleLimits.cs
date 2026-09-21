using System.Text.RegularExpressions;

namespace Crm.Segmentation.Domain.Rules;

/// <summary>The limits and patterns of the rule tree grammar (spec §5.1), in one place.</summary>
public static partial class RuleLimits
{
    public const int MaxDepth = 5;
    public const int MaxLeaves = 20;
    public const int MaxStringLength = 256;
    public const int MaxListLength = 100;
    public const int MaxEventNameLength = 128;
    public const int MaxWindowDays = 365;
    public const int MaxWindowHours = 8760;

    // \z, not $: `$` also matches before a trailing newline.
    [GeneratedRegex(@"^[A-Za-z0-9_]{1,64}\z", RegexOptions.CultureInvariant)]
    public static partial Regex KeyPattern();

    [GeneratedRegex(@"^(\d+)(d|h)\z", RegexOptions.CultureInvariant)]
    public static partial Regex WindowPattern();
}
