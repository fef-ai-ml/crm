using Crm.Kernel.Tenancy;
using Crm.Tenancy.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Crm.Tenancy.Infrastructure.Auth;

/// <summary>Copies the authenticated principal's tenant into the scoped <see cref="TenantContext"/>. A principal without <c>tenant_id</c> leaves it unresolved, so any data access fails closed.</summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenant, ILogger<TenantResolutionMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(logger);

        var user = context.User;
        if (user.Identity?.IsAuthenticated != true
            || !Guid.TryParse(user.FindFirst(TenancyClaims.TenantId)?.Value, out var tenantId)
            || tenantId == Guid.Empty)
        {
            await next(context);
            return;
        }

        var isApiKey = user.FindFirst(TenancyClaims.Kind)?.Value == TenancyClaims.KindApiKey;
        Guid? userId = !isApiKey && Guid.TryParse(user.FindFirst(TenancyClaims.Subject)?.Value, out var parsed) ? parsed : null;
        tenant.Resolve(tenantId, userId, isApiKey ? TenantPrincipalKind.ApiKey : TenantPrincipalKind.User);

        using (logger.BeginScope(new Dictionary<string, object?> { ["TenantId"] = tenantId, ["UserId"] = userId }))
        {
            await next(context);
        }
    }
}
