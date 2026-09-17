using System.Globalization;
using Crm.Tenancy.Infrastructure.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Crm.Api.OpenApi;

/// <summary>
/// Per-operation accuracy: drops responses the endpoint never declared (the `Task&lt;object&gt;` handler return type
/// otherwise causes minimal APIs to infer a spurious schema-less 200 alongside the real, explicitly-declared status),
/// and adds the `security` requirement plus 401/403 responses for endpoints that require authorization.
/// </summary>
public sealed class CrmOperationTransformer : IOpenApiOperationTransformer
{
    private const string Unauthorized = "401";
    private const string Forbidden = "403";
    private const string ProblemContentType = "application/problem+json";
    private const string ProblemSchemaId = nameof(ProblemDetails);

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        if (operation.Responses is not { } responses)
        {
            return Task.CompletedTask;
        }

        var metadata = context.Description.ActionDescriptor.EndpointMetadata;

        PruneUndeclaredResponses(responses, metadata);

        var authorizeData = metadata.OfType<IAuthorizeData>().ToList();
        var isAnonymous = metadata.OfType<IAllowAnonymous>().Any();
        if (authorizeData.Count > 0 && !isAnonymous)
        {
            AddSecurity(operation, responses, context, authorizeData);
        }

        ReorderResponsesByStatusCode(responses);
        return Task.CompletedTask;
    }

    private static void PruneUndeclaredResponses(OpenApiResponses responses, IList<object> metadata)
    {
        // A `Task<object>` handler return type makes minimal APIs itself add a second, spurious
        // IProducesResponseTypeMetadata for 200 with Type == typeof(object), alongside whatever the
        // endpoint's own .Produces<T>()/.Produces(n) calls declared. That inferred entry is not a real
        // declaration, so it must not count towards "genuinely declared" — otherwise pruning would never
        // remove the resulting schema-less 200 on endpoints whose real success status is 201/204.
        var declaredStatusCodes = metadata.OfType<IProducesResponseTypeMetadata>()
            .Where(m => m.Type != typeof(object))
            .Select(m => m.StatusCode.ToString(CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.Ordinal);

        if (declaredStatusCodes.Count is 0)
        {
            return;
        }

        foreach (var key in responses.Keys.Where(key => !declaredStatusCodes.Contains(key)).ToList())
        {
            responses.Remove(key);
        }
    }

    private static void AddSecurity(OpenApiOperation operation, OpenApiResponses responses, OpenApiOperationTransformerContext context, List<IAuthorizeData> authorizeData)
    {
        var schemeId = authorizeData.Any(a => string.Equals(a.Policy, AuthPolicies.IngestOnly, StringComparison.Ordinal))
            ? "apiKey"
            : "bearer";

        operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(schemeId, context.Document)] = [] }];

        // A direct component reference (rather than context.GetOrCreateSchemaAsync(typeof(ProblemDetails), ...))
        // guarantees `$ref: #/components/schemas/ProblemDetails` — the same shared schema every other problem
        // response in the document already points to — instead of a freshly generated, inlined duplicate schema.
        var problemSchema = new OpenApiSchemaReference(ProblemSchemaId, context.Document);

        AddProblemResponseIfMissing(responses, Unauthorized, "Authentication is required.", problemSchema);
        AddProblemResponseIfMissing(responses, Forbidden, "The caller is not allowed to do this.", problemSchema);
    }

    private static void AddProblemResponseIfMissing(OpenApiResponses responses, string statusCode, string description, IOpenApiSchema schema)
    {
        if (responses.ContainsKey(statusCode))
        {
            return;
        }

        responses[statusCode] = new OpenApiResponse
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                [ProblemContentType] = new OpenApiMediaType { Schema = schema },
            },
        };
    }

    private static void ReorderResponsesByStatusCode(OpenApiResponses responses)
    {
        var ordered = responses.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        responses.Clear();
        foreach (var (statusCode, response) in ordered)
        {
            responses[statusCode] = response;
        }
    }
}
