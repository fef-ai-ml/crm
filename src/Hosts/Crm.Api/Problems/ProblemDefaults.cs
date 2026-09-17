using System.Diagnostics;

namespace Crm.Api.Problems;

/// <summary>Applied to every problem response, including the bodiless 401/403/404/429 that UseStatusCodePages fills in.</summary>
public static class ProblemDefaults
{
    public static void Apply(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var problem = context.ProblemDetails;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
        problem.Instance ??= context.HttpContext.Request.Path;
        if (problem.Type is null || !problem.Type.StartsWith(ResultProblemMapper.TypeBase, StringComparison.Ordinal))
        {
            problem.Type = ResultProblemMapper.TypeFor(SlugFor(problem.Status ?? context.HttpContext.Response.StatusCode));
        }
    }

    private static string SlugFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "validation",
        StatusCodes.Status401Unauthorized => "unauthorized",
        StatusCodes.Status403Forbidden => "forbidden",
        StatusCodes.Status404NotFound => "notfound",
        StatusCodes.Status409Conflict => "conflict",
        StatusCodes.Status429TooManyRequests => "ratelimited",
        StatusCodes.Status503ServiceUnavailable => "unavailable",
        _ => "unexpected",
    };
}
