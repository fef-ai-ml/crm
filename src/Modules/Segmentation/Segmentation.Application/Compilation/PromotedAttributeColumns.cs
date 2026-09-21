namespace Crm.Segmentation.Application.Compilation;

/// <summary>Attribute keys stored as computed columns instead of a `JSON_VALUE` lookup (H18), for the hottest
/// filters. Column names are `Attr_&lt;key&gt;`.</summary>
public static class PromotedAttributeColumns
{
    private static readonly Dictionary<string, string> Columns = new(StringComparer.Ordinal)
    {
        ["plan"] = "Attr_plan",
        ["country"] = "Attr_country",
        ["city"] = "Attr_city",
    };

    public static bool TryGetColumn(string key, out string column) => Columns.TryGetValue(key, out column!);
}
