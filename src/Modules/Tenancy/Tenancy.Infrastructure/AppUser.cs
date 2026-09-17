using Microsoft.AspNetCore.Identity;

namespace Crm.Tenancy.Infrastructure;

/// <summary>Dashboard user. Belongs to exactly one tenant; ids are UUIDv7 strings so <c>sub</c> parses as a Guid.</summary>
public sealed class AppUser : IdentityUser
{
    public AppUser()
    {
        Id = Guid.CreateVersion7().ToString();
    }

    public Guid TenantId { get; set; }

    public string? DisplayName { get; set; }
}
