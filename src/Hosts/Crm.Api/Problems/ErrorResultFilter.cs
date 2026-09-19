using System.Diagnostics;
using System.Globalization;
using Crm.Kernel;

namespace Crm.Api.Problems;

/// <summary>Module handlers return an <see cref="IResult"/> or a Kernel <see cref="Error"/>; this filter renders the latter as problem+json.</summary>
public sealed class ErrorResultFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var result = await next(context);
        if (result is not Error error)
        {
            return result;
        }

        var http = context.HttpContext;
        if (error.Kind == ErrorKind.Unavailable)
        {
            http.Response.Headers.RetryAfter = ResultProblemMapper.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }

        var traceId = Activity.Current?.Id ?? http.TraceIdentifier;
        return Results.Problem(ResultProblemMapper.Map(error, traceId, http.Request.Path));
    }
}
