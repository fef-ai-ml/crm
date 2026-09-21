namespace Crm.Tenancy.Infrastructure.Auth;

public static class AuthSchemes
{
    public const string Bearer = "Bearer";
    public const string ApiKey = "ApiKey";
    public const string Smart = "CrmSmart";
    public const string ApiKeyHeader = "X-Api-Key";
}

public static class AuthPolicies
{
    public const string AdminOnly = "AdminOnly";
    public const string AdminOrViewer = "AdminOrViewer";
    public const string IngestOnly = "IngestOnly";
}

public static class RateLimitPolicies
{
    public const string Ingest = "ingest";
    public const string Auth = "auth";
}

public static class EndpointTags
{
    public const string Ingest = "Ingest";
    public const string Tenancy = "Tenancy";
    public const string Profiles = "Profiles";
    public const string Segments = "Segments";
}
