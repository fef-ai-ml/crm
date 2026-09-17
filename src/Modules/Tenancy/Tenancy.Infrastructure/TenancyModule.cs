using System.Globalization;
using System.Threading.RateLimiting;
using Crm.Kernel.Data;
using Crm.Tenancy.Application;
using Crm.Tenancy.Domain;
using Crm.Tenancy.Infrastructure.Auth;
using Crm.Tenancy.Infrastructure.Tenants;
using Crm.Tenancy.Infrastructure.Tokens;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();
        services.AddSingleton<ITokenService, TokenService>();

        services.AddSingleton<ApiKeyUsageTracker>();
        services.AddScoped<IApiKeyAuthenticator, ApiKeyAuthenticator>();
        services.AddScoped<AuthService>();
        services.AddScoped<TenantBootstrapService>();

        services.AddAuthentication(AuthSchemes.Smart)
            .AddPolicyScheme(AuthSchemes.Smart, AuthSchemes.Smart, o => o.ForwardDefaultSelector = SelectScheme)
            .AddJwtBearer(AuthSchemes.Bearer, _ => { })
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(AuthSchemes.ApiKey, _ => { });
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

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) =>
            {
                var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                    : 60;
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
            limiter.AddPolicy(RateLimitPolicies.Ingest, http =>
            {
                var prefix = http.User.FindFirst(TenancyClaims.KeyPrefix)?.Value;
                return prefix is null
                    ? RateLimitPartition.GetNoLimiter("no-api-key")
                    : RateLimitPartition.GetTokenBucketLimiter(prefix, _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 2000,
                        TokensPerPeriod = 2000,
                        ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    });
            });
        });

        return services;
    }

    private static string SelectScheme(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthSchemes.Bearer;
        }

        return context.Request.Headers.ContainsKey(AuthSchemes.ApiKeyHeader) ? AuthSchemes.ApiKey : AuthSchemes.Bearer;
    }
}
