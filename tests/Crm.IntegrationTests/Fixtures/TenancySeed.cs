using System.Text;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Tenancy.Application;
using Crm.Tenancy.Domain;
using Crm.Tenancy.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Crm.IntegrationTests.Fixtures;

public sealed record SeededTenant(Guid TenantId, string Slug, string AdminUserId, string AdminEmail, string ViewerUserId, string ViewerEmail);

public sealed record SeededApiKey(Guid Id, string Prefix, string Plaintext);

/// <summary>Arranges tenants, users, keys and tokens through the API's own services, so tests stay independent of the endpoints under test.</summary>
public static class TenancySeed
{
    public const string Password = "Test-Passw0rd!";

    public static async Task<SeededTenant> CreateTenantAsync(CrmApiFactory factory, string? slug = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        slug ??= "t-" + Guid.CreateVersion7().ToString("N")[..20];

        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<TenancyDbContext>();
        var clock = services.GetRequiredService<IClock>();

        var tenant = Tenant.Create($"Tenant {slug}", slug, clock.UtcNow).Value;
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        services.GetRequiredService<TenantContext>().Resolve(tenant.Id, null, TenantPrincipalKind.None);
        var users = services.GetRequiredService<UserManager<AppUser>>();
        var admin = await CreateUserAsync(users, tenant.Id, $"admin@{slug}.test", Roles.Admin);
        var viewer = await CreateUserAsync(users, tenant.Id, $"viewer@{slug}.test", Roles.Viewer);
        return new SeededTenant(tenant.Id, slug, admin.Id, admin.Email!, viewer.Id, viewer.Email!);
    }

    public static async Task<SeededApiKey> AddApiKeyAsync(CrmApiFactory factory, SeededTenant tenant, string name = "sdk")
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(tenant);

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(tenant.TenantId, null, TenantPrincipalKind.None);
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var generated = ApiKey.Generate(name, tenant.AdminUserId);
        db.ApiKeys.Add(generated.Entity);
        await db.SaveChangesAsync();
        return new SeededApiKey(generated.Entity.Id, generated.Entity.Prefix, generated.Plaintext);
    }

    public static string AccessToken(CrmApiFactory factory, string userId, Guid tenantId, params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory.Services.GetRequiredService<ITokenService>().IssueAccessToken(userId, tenantId, roles).Value;
    }

    /// <summary>A correctly signed JWT that lacks <c>tenant_id</c> (IT-032).</summary>
    public static string TokenWithoutTenant(CrmApiFactory factory, string userId, string role)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var options = factory.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            Claims = new Dictionary<string, object> { [TenancyClaims.Subject] = userId, [TenancyClaims.Role] = role },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)), SecurityAlgorithms.HmacSha256),
        });
    }

    private static async Task<AppUser> CreateUserAsync(UserManager<AppUser> users, Guid tenantId, string email, string role)
    {
        var user = new AppUser { TenantId = tenantId, UserName = email, Email = email, EmailConfirmed = true };
        Ensure(await users.CreateAsync(user, Password));
        Ensure(await users.AddToRoleAsync(user, role));
        return user;
    }

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}")));
        }
    }
}
