namespace Crm.Tenancy.Application;

public static class TenancyClaims
{
    public const string Subject = "sub";
    public const string TenantId = "tenant_id";
    public const string Role = "role";
    public const string TokenId = "jti";
    public const string Kind = "kind";
    public const string KeyPrefix = "key_prefix";
    public const string KeyId = "key_id";

    public const string KindUser = "user";
    public const string KindApiKey = "apikey";
}
