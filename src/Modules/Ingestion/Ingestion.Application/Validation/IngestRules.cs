using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Crm.Ingestion.Application.Validation;

/// <summary>The limits of the ingest contract (spec §4.1, docs/api/README.md), in one place.</summary>
public static partial class IngestRules
{
    public const int MaxJsonBytes = 32 * 1024;
    public const int MaxUserIdLength = 128;       // profiles.Profiles.ExternalId
    public const int MaxAnonymousIdLength = 256;  // profiles.UserIdentities.Value
    public const int MaxBatchItems = 500;
    public static readonly TimeSpan TimestampWindow = TimeSpan.FromDays(7);

    public const string EventNameMessage = "must match [A-Za-z0-9_]{1,64}";
    public const string ObjectMessage = "must be a JSON object";
    public const string SizeMessage = "must be at most 32 KB";

    // \z, not $: `$` also matches before a trailing newline.
    [GeneratedRegex(@"^[A-Za-z0-9_]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex EventName();

    public static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    public static bool IsValidEventName(string? value) => value is not null && EventName().IsMatch(value);

    public static bool IsObjectOrAbsent(JsonElement? element)
        => element is null || element.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined;

    /// <summary>Size of the JSON text as received, in UTF-8 bytes. Non-objects are the other rule's business.</summary>
    public static bool FitsSizeLimit(JsonElement? element)
        => element is null
           || element.Value.ValueKind is not JsonValueKind.Object
           || Encoding.UTF8.GetByteCount(element.Value.GetRawText()) <= MaxJsonBytes;

    public static bool IsWithinWindow(DateTimeOffset? timestamp, DateTime utcNow)
        => timestamp is null || (timestamp.Value.UtcDateTime - utcNow).Duration() <= TimestampWindow;
}
