using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Api.Problems;

/// <summary>Last line of defence: log the exception (IT-032 relies on TenantContextMissingException surfacing here) and answer 500 problem+json.</summary>
public sealed partial class UnhandledExceptionHandler(IProblemDetailsService problems, ILogger<UnhandledExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        if (exception is BadHttpRequestException bad)
        {
            var path = httpContext.Request.Path;
            LogBadRequest(logger, bad);

            httpContext.Response.StatusCode = bad.StatusCode;
            return await problems.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                Exception = exception,
                ProblemDetails = new ProblemDetails
                {
                    Status = bad.StatusCode,
                    // Only a 400 gets the "validation" slug; every other BadHttpRequestException status (for
                    // example 413 from a request size limit) is a distinct problem type of its own.
                    Type = bad.StatusCode == StatusCodes.Status400BadRequest
                        ? ResultProblemMapper.TypeFor("validation")
                        : ResultProblemMapper.TypeFor("badrequest"),
                    Title = "The request could not be read.",
                    Detail = bad.Message,
                    Instance = path,
                },
            });
        }

        LogUnhandled(logger, exception, exception.GetType().Name, httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Type = ResultProblemMapper.TypeFor("unexpected"),
                Title = "An unexpected error occurred.",
                Instance = httpContext.Request.Path,
            },
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled {ExceptionType} on {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string exceptionType, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Bad request")]
    private static partial void LogBadRequest(ILogger logger, Exception exception);
}
