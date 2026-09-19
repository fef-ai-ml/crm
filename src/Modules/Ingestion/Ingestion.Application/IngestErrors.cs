using Crm.Kernel;

namespace Crm.Ingestion.Application;

public static class IngestErrors
{
    public const string InvalidCode = "ingest.invalid";
    public const string UnavailableCode = "ingest.unavailable";

    /// <summary>`detail` names the first failing field the way docs/api/README.md shows it: "event must match …".</summary>
    public static Error Invalid(IReadOnlyDictionary<string, string[]> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var first = fields.First();
        return Error.Validation(InvalidCode, $"{first.Key} {first.Value[0]}", fields);
    }

    public static Error Invalid(string field, string message)
        => Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });

    public static readonly Error Unavailable = Error.Unavailable(UnavailableCode, "The event stream is temporarily unavailable; retry the request.");
}
