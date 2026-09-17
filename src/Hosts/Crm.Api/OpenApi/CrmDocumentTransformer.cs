using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Crm.Api.OpenApi;

/// <summary>Stable metadata: title/version, no servers (export must not depend on the machine), and the two credential schemes.</summary>
public sealed class CrmDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        document.Info = new OpenApiInfo
        {
            Title = "CRM Platform API",
            Version = "v1",
            Description = "Multi-tenant CRM: ingestion, profiles, segmentation. Errors are application/problem+json.",
        };
        document.Servers = [];

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Dashboard users: 15-minute access token from POST /v1/auth/login.",
        };
        document.Components.SecuritySchemes["apiKey"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "X-Api-Key",
            Description = "Ingest clients: crm_live_<32 base62>.",
        };
        return Task.CompletedTask;
    }
}
