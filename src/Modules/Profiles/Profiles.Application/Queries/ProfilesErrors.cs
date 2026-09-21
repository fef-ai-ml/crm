using Crm.Kernel;

namespace Crm.Profiles.Application.Queries;

public static class ProfilesErrors
{
    public const string NotFoundCode = "profile.not_found";
    public const string InvalidQueryCode = "profiles.invalid_query";
    public const string InvalidFilterCode = "profiles.invalid_filter";

    /// <summary>One answer for a missing id, another tenant's id and a merge loser: nothing leaks (ruling F8).</summary>
    public static readonly Error NotFound = Error.NotFound(NotFoundCode, "Profile not found.");

    public static Error InvalidQuery(string message)
        => Error.Validation(InvalidQueryCode, $"q {message}", new Dictionary<string, string[]>(StringComparer.Ordinal) { ["q"] = [message] });

    public static Error InvalidFilter(string field, string message)
        => Error.Validation(InvalidFilterCode, $"{field} {message}", new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });
}
