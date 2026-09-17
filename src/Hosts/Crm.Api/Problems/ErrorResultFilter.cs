using System.Diagnostics;
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
        var traceId = Activity.Current?.Id ?? http.TraceIdentifier;
        return Results.Problem(ResultProblemMapper.Map(error, traceId, http.Request.Path));
    }
}
