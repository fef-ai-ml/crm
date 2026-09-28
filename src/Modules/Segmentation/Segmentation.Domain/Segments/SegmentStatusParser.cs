namespace Crm.Segmentation.Domain.Segments;

/// <summary>Total parse for <see cref="SegmentStatus"/>: accepts only the three names, case-insensitively.
/// <c>Enum.TryParse</c> alone is not a total guard here — it also accepts the string form of any integer
/// (<c>"7"</c>, <c>"1"</c>) and, because <see cref="SegmentStatus"/> is not a <c>[Flags]</c> enum but
/// <c>TryParse</c> does not special-case that, a comma-joined list of names (<c>"Draft,Active"</c>) as a
/// bitwise OR (I-1/M-1). One shared helper for every path that turns request/query text into a
/// <see cref="SegmentStatus"/> (<c>EfSegmentService</c>'s create/update, <c>EfSegmentQueries</c>'s list
/// filter) so the guard is written once.</summary>
public static class SegmentStatusParser
{
    public static bool TryParse(string? value, out SegmentStatus status) => EnumNames.TryParse(value, out status);
}

/// <summary>Plan I, ruling I15: the same total parse for the `source` filter of GET /segments/{id}/changes —
/// exactly `Batch` or `Realtime`, case-insensitively.</summary>
public static class MembershipChangeSourceParser
{
    public static bool TryParse(string? value, out MembershipChangeSource source) => EnumNames.TryParse(value, out source);
}

/// <summary>Matches a value against the enum's declared names only (see
/// <see cref="SegmentStatusParser"/> for why <c>Enum.TryParse</c> is not enough).</summary>
internal static class EnumNames
{
    public static bool TryParse<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum
    {
        if (value is not null)
        {
            foreach (var name in Enum.GetNames<TEnum>())
            {
                if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
                {
                    result = Enum.Parse<TEnum>(name);
                    return true;
                }
            }
        }

        result = default;
        return false;
    }
}
