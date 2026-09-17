using Crm.Kernel.Data;
using Crm.Tenancy.Application;
using Crm.Tenancy.Domain;
using Crm.Tenancy.Infrastructure.Auth;
using Crm.Tenancy.Infrastructure.Tokens;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Crm.Tenancy.Infrastructure;

public static class TenancyModule
{
    /// <summary>Registers persistence and ASP.NET Core Identity for the Tenancy module.</summary>
    public static IServiceCollection AddTenancyModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString("Crm");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:Crm is not configured.");
        }

        services.AddDbContext<TenancyDbContext>((sp, options) => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(TenancyDbContext.MigrationsHistoryTable, TenancyDbContext.Schema))
            .AddInterceptors(sp.GetRequiredService<TenantSaveChangesInterceptor>()));

        services.AddIdentityCore<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 10;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<TenancyDbContext>();

        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .Validate(o => o.IsValid(out _), $"Auth options are invalid: signing key must be at least {AuthOptions.MinSigningKeyBytes} bytes; issuer, audience and lifetimes are required.")
            .ValidateOnStart();
        services.AddSingleton<ITokenService, TokenService>();

        services.AddAuthentication(AuthSchemes.Bearer).AddJwtBearer(AuthSchemes.Bearer, _ => { });
        services.AddOptions<JwtBearerOptions>(AuthSchemes.Bearer)
            .Configure<IOptions<AuthOptions>>((jwt, auth) =>
            {
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = TokenService.CreateValidationParameters(auth.Value);
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.AdminOnly, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin))
            .AddPolicy(AuthPolicies.AdminOrViewer, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin, Roles.Viewer))
            .AddPolicy(AuthPolicies.IngestOnly, p => p.RequireAuthenticatedUser().RequireClaim(TenancyClaims.Kind, TenancyClaims.KindApiKey));

        return services;
    }
}
