using Microsoft.AspNetCore.Http;

namespace Crm.Tenancy.Infrastructure.Auth;

/// <summary><c>crm_refresh</c>: HttpOnly, Secure, SameSite=Strict, Path=/v1/auth. Secure is relaxed only for plain-HTTP localhost in Development.</summary>
public static class RefreshCookie
{
    public const string Name = "crm_refresh";
    public const string Path = "/v1/auth";

    public static void Write(HttpContext http, string token, DateTime expiresUtc, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(http);
        var options = Options(http, isDevelopment);
        options.Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresUtc, DateTimeKind.Utc));
        http.Response.Cookies.Append(Name, token, options);
    }

    public static void Clear(HttpContext http, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Response.Cookies.Delete(Name, Options(http, isDevelopment));
    }

    private static CookieOptions Options(HttpContext http, bool isDevelopment)
    {
        var host = http.Request.Host.Host;
        var plainLocalhost = !http.Request.IsHttps
            && (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || host == "127.0.0.1"
                || host == "::1"
                || host == "[::1]");
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = !(isDevelopment && plainLocalhost),
            SameSite = SameSiteMode.Strict,
            Path = Path,
            IsEssential = true,
        };
    }
}
