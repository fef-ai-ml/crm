using System.Text.Json;
using Crm.Hosting.Health;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Crm.Api.UnitTests.Hosting;

[Trait("Category", "Unit")]
public class HealthResponseWriterTests
{
    [Fact]
    public async Task Writes_status_and_per_check_status_as_json()
    {
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["sqlserver"] = new(HealthStatus.Healthy, null, TimeSpan.Zero, null, null),
                ["redis"] = new(HealthStatus.Unhealthy, "timeout", TimeSpan.Zero, null, null),
            },
            HealthStatus.Unhealthy,
            TimeSpan.Zero);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await HealthResponseWriter.WriteAsync(context, report);

        context.Response.ContentType.Should().StartWith("application/json");
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        doc.RootElement.GetProperty("status").GetString().Should().Be("Unhealthy");
        doc.RootElement.GetProperty("checks").GetProperty("sqlserver").GetString().Should().Be("Healthy");
        doc.RootElement.GetProperty("checks").GetProperty("redis").GetString().Should().Be("Unhealthy");
    }
}
