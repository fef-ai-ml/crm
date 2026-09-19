using FluentValidation.Results;

namespace Crm.Ingestion.Application.Validation;

public static class ValidationFields
{
    /// <summary>FluentValidation failures → the `errors` object of a problem response: field path → messages.</summary>
    public static IReadOnlyDictionary<string, string[]> From(ValidationResult result, string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Errors
            .GroupBy(failure => prefix + failure.PropertyName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(f => f.ErrorMessage).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }
}
