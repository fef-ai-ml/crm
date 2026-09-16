using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crm.Contracts;

public static class ContractJson
{
    /// <summary>Wire format: camelCase, nulls omitted, case-insensitive reads.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    /// <summary>Same contract, indented — used only for human-readable snapshots.</summary>
    public static readonly JsonSerializerOptions IndentedOptions = new(Options) { WriteIndented = true };
}
