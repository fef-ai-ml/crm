using Crm.Kernel;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Tenancy.Application;
using Crm.Tenancy.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Crm.Tenancy.Infrastructure.Tenants;

public sealed record BootstrapTenantCommand(string Name, string Slug, string AdminEmail, string AdminPassword, string? ViewerEmail, string? ViewerPassword);

/// <summary>Development-only: one transaction creates the tenant, its Admin and, optionally, a Viewer.</summary>
public sealed class TenantBootstrapService(TenancyDbContext db, UserManager<AppUser> users, TenantContext tenantContext, IClock clock)
{
    public async Task<Result<Tenant>> CreateAsync(BootstrapTenantCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (tenantContext.IsResolved)
        {
            return Error.Forbidden("tenant.bootstrap_authenticated", "Bootstrap must be called without credentials.");
        }

        var created = Tenant.Create(command.Name, command.Slug, clock.UtcNow);
        if (created.IsFailure)
        {
            return created.Error!;
        }

        if (await db.Tenants.AnyAsync(t => t.Slug == command.Slug, cancellationToken))
        {
            return TenancyErrors.SlugTaken;
        }

        var emails = new[] { command.AdminEmail, command.ViewerEmail }.Where(e => e is not null).Select(e => users.NormalizeEmail(e)).ToList();
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => emails.Contains(u.NormalizedEmail), cancellationToken))
        {
            return TenancyErrors.EmailTaken;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var tenant = created.Value;
        db.Tenants.Add(tenant);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent bootstrap won the race on the same slug between the pre-check above and this
            // insert; UX_Tenants_Slug is the only unique index this insert can violate.
            await transaction.RollbackAsync(cancellationToken);
            return TenancyErrors.SlugTaken;
        }

        tenantContext.Resolve(tenant.Id, null, TenantPrincipalKind.None);

        Error? failure;
        try
        {
            failure = await CreateUserAsync(tenant.Id, command.AdminEmail, command.AdminPassword, Roles.Admin, "adminPassword", "adminEmail");
            if (failure is null && command.ViewerEmail is not null && command.ViewerPassword is not null)
            {
                failure = await CreateUserAsync(tenant.Id, command.ViewerEmail, command.ViewerPassword, Roles.Viewer, "viewerPassword", "viewerEmail");
            }
        }
        catch (DbUpdateException)
        {
            // A concurrent bootstrap (racing on the same email) won between the pre-check above and
            // Identity's own insert; EmailIndex is the unique index this can violate.
            await transaction.RollbackAsync(cancellationToken);
            return TenancyErrors.EmailTaken;
        }

        if (failure is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return failure;
        }

        await transaction.CommitAsync(cancellationToken);
        return tenant;
    }

    private async Task<Error?> CreateUserAsync(Guid tenantId, string email, string password, string role, string passwordField, string emailField)
    {
        var user = new AppUser { TenantId = tenantId, UserName = email, Email = email, EmailConfirmed = true };
        var result = await users.CreateAsync(user, password);
        if (result.Succeeded)
        {
            result = await users.AddToRoleAsync(user, role);
        }

        return result.Succeeded ? null : TenancyErrors.IdentityFailure(result.Errors.Select(e => (e.Code, e.Description)), passwordField, emailField);
    }
}
