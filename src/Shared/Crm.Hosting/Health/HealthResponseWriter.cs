using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Crm.Hosting.Health;

public static class HealthResponseWriter
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Body shape: { "status": "Healthy", "checks": { "sqlserver": "Healthy", ... } }.</summary>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString(), StringComparer.Ordinal),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, Options));
    }
}
