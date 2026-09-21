using Crm.Kernel;
using Crm.Segmentation.Domain.Rules;

namespace Crm.Segmentation.Application.Segments;

public static class SegmentErrors
{
    public const string NotFoundCode = "segment.not_found";
    public const string NameTakenCode = "segment.name_taken";
    public const string ArchivedCode = "segment.archived";
    public const string InvalidDefinitionCode = "segments.invalid_definition";
    public const string InvalidRequestCode = "segments.invalid_request";
    public const string InvalidFilterCode = "segments.invalid_filter";

    /// <summary>One answer for a missing id and another tenant's id: nothing about a segment's existence leaks (mirrors ProfilesErrors.NotFound / ruling F8).</summary>
    public static readonly Error NotFound = Error.NotFound(NotFoundCode, "Segment not found.");

    public static readonly Error NameTaken = Error.Conflict(NameTakenCode, "A segment with this name already exists.");

    /// <summary>Reserved for a write that targets an already-archived segment (e.g. queuing a run, task 7); unused by this task's create/update/archive paths, which allow any status transition (H15).</summary>
    public static readonly Error Archived = Error.Conflict(ArchivedCode, "The segment is archived.");

    /// <summary>Field paths and messages come straight from <see cref="RuleParser"/> (H24) — never rewritten.</summary>
    public static Error InvalidDefinition(IReadOnlyList<RuleError> errors)
    {
        var fields = errors
            .GroupBy(e => e.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray(), StringComparer.Ordinal);
        return Error.Validation(InvalidDefinitionCode, "The rule definition is invalid.", fields);
    }

    public static Error InvalidRequest(IReadOnlyDictionary<string, string[]> fields)
        => Error.Validation(InvalidRequestCode, "The request is invalid.", fields);

    public static Error InvalidFilter(string message)
        => Error.Validation(InvalidFilterCode, $"status {message}", new Dictionary<string, string[]>(StringComparer.Ordinal) { ["status"] = [message] });
}
