using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Formatting.Compact;

namespace Crm.Hosting;

public static class SerilogExtensions
{
    /// <summary>Structured JSON console logging with the application name; TenantId/TraceId are pushed via LogContext by middleware.</summary>
    public static IHostApplicationBuilder AddCrmSerilog(this IHostApplicationBuilder builder, string applicationName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSerilog((services, configuration) => configuration
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", applicationName)
            .WriteTo.Console(new CompactJsonFormatter()));
        return builder;
    }
}
