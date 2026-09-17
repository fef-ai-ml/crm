using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Crm.Api.UnitTests;

/// <summary>CT-001 (plan B scope): the committed document lists exactly the Tenancy operations. Freshness is enforced by the CI drift step.</summary>
public class OpenApiDocumentTests
{
    private static readonly string[] Expected =
    [
        "DELETE /v1/api-keys/{id}",
        "GET /v1/api-keys",
        "POST /v1/api-keys",
        "POST /v1/auth/login",
        "POST /v1/auth/logout",
        "POST /v1/auth/refresh",
        "POST /v1/tenants",
    ];

    [Fact]
    public void CT001_committed_document_lists_exactly_the_tenancy_operations()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "docs/api/openapi.json")));

        var operations = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(op => op.Name is "get" or "post" or "put" or "patch" or "delete")
                .Select(op => $"{op.Name.ToUpperInvariant()} {path.Name}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        operations.Should().Equal(Expected);
        document.RootElement.GetProperty("info").GetProperty("title").GetString().Should().Be("CRM Platform API");
        var schemes = document.RootElement.GetProperty("components").GetProperty("securitySchemes");
        schemes.TryGetProperty("bearer", out _).Should().BeTrue();
        schemes.TryGetProperty("apiKey", out _).Should().BeTrue();
        if (document.RootElement.TryGetProperty("servers", out var servers))
        {
            servers.GetArrayLength().Should().Be(0, "the document must not depend on where it was exported");
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Crm.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Crm.sln not found above the test directory.");
    }
}
