namespace Crm.Profiles.Infrastructure.Persistence;

/// <summary>Slice 1 ships a fixed hot-key list (spec §3.1); each key gets a persisted computed column and an index.</summary>
internal static class PromotedAttributes
{
    public static readonly IReadOnlyList<string> Keys = ["plan", "country", "city"];

    public static string ColumnName(string key) => $"Attr_{key}";

    public static string ColumnSql(string key) => $"CAST(JSON_VALUE([Attributes], '$.{key}') AS nvarchar(256))";
}
