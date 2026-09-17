namespace Crm.Api.OpenApi;

/// <summary>Security scheme ids shared by <see cref="CrmDocumentTransformer"/> and <see cref="CrmOperationTransformer"/>,
/// so a typo in one cannot silently produce a dangling <c>$ref</c> in the other.</summary>
internal static class OpenApiSchemeIds
{
    public const string Bearer = "bearer";
    public const string ApiKey = "apiKey";
}
