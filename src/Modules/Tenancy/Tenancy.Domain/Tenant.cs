using System.Text.RegularExpressions;
using Crm.Kernel;

namespace Crm.Tenancy.Domain;

public enum TenantStatus
{
    Active,
    Suspended,
}

/// <summary>The one aggregate without a TenantId: its Id is the tenant id everywhere else.</summary>
public sealed partial class Tenant
{
    public const int NameMaxLength = 128;

    private Tenant()
    {
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public string Name { get; private set; } = string.Empty;

    public string Slug { get; private set; } = string.Empty;

    public TenantStatus Status { get; private set; } = TenantStatus.Active;

    public DateTime CreatedAt { get; private set; }

    public void Suspend() => Status = TenantStatus.Suspended;

    public void Activate() => Status = TenantStatus.Active;

    [GeneratedRegex("^[a-z0-9-]{3,64}$")]
    private static partial Regex SlugPattern();

    public static Result<Tenant> Create(string name, string slug, DateTime utcNow)
    {
        var fields = new Dictionary<string, string[]>();
        var trimmedName = name?.Trim() ?? string.Empty;
        if (trimmedName.Length is 0 or > NameMaxLength)
        {
            fields["name"] = [$"must be 1 to {NameMaxLength} characters"];
        }

        if (slug is null || !SlugPattern().IsMatch(slug))
        {
            fields["slug"] = ["must match [a-z0-9-]{3,64}"];
        }

        if (fields.Count > 0)
        {
            return Error.Validation("tenant.invalid", "The tenant is not valid.", fields);
        }

        return new Tenant { Name = trimmedName, Slug = slug!, CreatedAt = utcNow };
    }
}
