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

    private static readonly ExpectedOperation[] ExpectedOperations =
    [
        new("POST", "/v1/auth/login", ["200", "400", "401"], RequiresBearer: false),
        new("POST", "/v1/auth/refresh", ["200", "401"], RequiresBearer: false),
        new("POST", "/v1/auth/logout", ["204", "401", "403"], RequiresBearer: true),
        new("POST", "/v1/tenants", ["201", "400", "409"], RequiresBearer: false),
        new("GET", "/v1/api-keys", ["200", "401", "403"], RequiresBearer: true),
        new("POST", "/v1/api-keys", ["201", "400", "401", "403"], RequiresBearer: true),
        new("DELETE", "/v1/api-keys/{id}", ["204", "401", "403", "404"], RequiresBearer: true),
    ];

    [Fact]
    public void CT001_operations_declare_exactly_their_responses_and_security()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "docs/api/openapi.json")));
        var paths = document.RootElement.GetProperty("paths");

        foreach (var expected in ExpectedOperations)
        {
            var operation = paths.GetProperty(expected.Path).GetProperty(expected.Method.ToLowerInvariant());
            var responses = operation.GetProperty("responses");

            var statusCodes = responses.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
            statusCodes.Should().Equal(expected.StatusCodes.Order(StringComparer.Ordinal), $"{expected.Method} {expected.Path}");

            var hasSecurity = operation.TryGetProperty("security", out var security);
            if (expected.RequiresBearer)
            {
                hasSecurity.Should().BeTrue($"{expected.Method} {expected.Path} should require authentication");
                security.GetArrayLength().Should().Be(1);
                var requirement = security.EnumerateArray().Single();
                requirement.EnumerateObject().Select(p => p.Name).Should().Equal("bearer");
            }
            else
            {
                hasSecurity.Should().BeFalse($"{expected.Method} {expected.Path} should be anonymous");
            }

            foreach (var response in responses.EnumerateObject().Where(r => r.Name.StartsWith('4')))
            {
                response.Value.TryGetProperty("content", out var content)
                    .Should().BeTrue($"{expected.Method} {expected.Path} {response.Name} should have a body");
                content.EnumerateObject().Select(p => p.Name).Should()
                    .Equal(["application/problem+json"], $"{expected.Method} {expected.Path} {response.Name}");
            }
        }
    }

    [Fact]
    public void CT001_no_response_schema_is_empty()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "docs/api/openapi.json")));

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject().Where(op => op.Name is "get" or "post" or "put" or "patch" or "delete"))
            {
                foreach (var response in operation.Value.GetProperty("responses").EnumerateObject())
                {
                    if (!response.Value.TryGetProperty("content", out var content))
                    {
                        continue;
                    }

                    foreach (var mediaType in content.EnumerateObject())
                    {
                        if (mediaType.Value.TryGetProperty("schema", out var schema))
                        {
                            schema.EnumerateObject().Should().NotBeEmpty($"{operation.Name.ToUpperInvariant()} {path.Name} {response.Name} {mediaType.Name}");
                        }
                    }
                }
            }
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

    private sealed record ExpectedOperation(string Method, string Path, string[] StatusCodes, bool RequiresBearer);
}
