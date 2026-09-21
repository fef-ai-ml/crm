namespace Crm.Kernel.Paging;

/// <summary>Errors every paginated endpoint shares (docs/api/README.md, Pagination).</summary>
public static class PagingErrors
{
    public const string InvalidLimitCode = "pagination.invalid_limit";
    public const string InvalidCursorCode = "pagination.invalid_cursor";

    public static readonly Error InvalidLimit = Error.Validation(
        InvalidLimitCode,
        $"limit must be between 1 and {PageLimit.Max}",
        new Dictionary<string, string[]>(StringComparer.Ordinal) { ["limit"] = [$"must be between 1 and {PageLimit.Max}"] });

    public static readonly Error InvalidCursor = Error.Validation(
        InvalidCursorCode,
        "cursor is not a value returned by this endpoint",
        new Dictionary<string, string[]>(StringComparer.Ordinal) { ["cursor"] = ["is not a value returned by this endpoint"] });
}
