namespace Crm.Tenancy.Application;

public sealed record AccessToken(string Value, int ExpiresInSeconds, DateTime ExpiresAtUtc);

public interface ITokenService
{
    AccessToken IssueAccessToken(string userId, Guid tenantId, IReadOnlyCollection<string> roles);
}
