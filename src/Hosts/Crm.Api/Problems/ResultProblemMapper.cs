using Crm.Kernel;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Api.Problems;

/// <summary>Kernel <see cref="Error"/> → RFC 9457 problem details. Status, type, title and <c>errors</c> are decided here and nowhere else.</summary>
public static class ResultProblemMapper
{
    public const string TypeBase = "https://crm.example/problems/";

    public static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static string TypeFor(string kindLowercase) => TypeBase + kindLowercase;

    public static ProblemDetails Map(Error error, string traceId, string? instance = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        var problem = new ProblemDetails
        {
            Status = StatusFor(error.Kind),
            Type = TypeFor(KindSlug(error.Kind)),
            Title = TitleFor(error.Kind),
            Detail = error.Message,
            Instance = instance,
        };
        problem.Extensions["traceId"] = traceId;
        problem.Extensions["code"] = error.Code;
        if (error.Kind == ErrorKind.Validation)
        {
            problem.Extensions["errors"] = error.Fields ?? new Dictionary<string, string[]>();
        }

        return problem;
    }

    private static string KindSlug(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => "validation",
        ErrorKind.Unauthorized => "unauthorized",
        ErrorKind.Forbidden => "forbidden",
        ErrorKind.NotFound => "notfound",
        ErrorKind.Conflict => "conflict",
        _ => "unexpected",
    };

    private static string TitleFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => "One or more validation errors occurred.",
        ErrorKind.Unauthorized => "Authentication is required.",
        ErrorKind.Forbidden => "The caller is not allowed to do this.",
        ErrorKind.NotFound => "The resource was not found.",
        ErrorKind.Conflict => "The request conflicts with the current state.",
        _ => "An unexpected error occurred.",
    };
}
