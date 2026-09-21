using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Crm.Api.UnitTests;

/// <summary>CT-001: the committed document lists exactly the implemented operations. Freshness is enforced by the CI drift step.</summary>
public class OpenApiDocumentTests
{
    private static readonly string[] Expected =
    [
        "DELETE /v1/api-keys/{id}",
        "GET /v1/api-keys",
        "GET /v1/profiles",
        "GET /v1/profiles/{id}",
        "GET /v1/profiles/{id}/events",
        "POST /v1/api-keys",
        "POST /v1/auth/login",
        "POST /v1/auth/logout",
        "POST /v1/auth/refresh",
        "POST /v1/batch",
        "POST /v1/identify",
        "POST /v1/tenants",
        "POST /v1/track",
    ];

    [Fact]
    public void CT001_committed_document_lists_exactly_the_implemented_operations()
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
        new("POST", "/v1/auth/login", ["200", "400", "401", "429"], Security: null),
        new("POST", "/v1/auth/refresh", ["200", "401", "429"], Security: null),
        new("POST", "/v1/auth/logout", ["204", "401", "403"], Security: "bearer"),
        new("POST", "/v1/tenants", ["201", "400", "409", "429"], Security: null),
        new("GET", "/v1/api-keys", ["200", "401", "403"], Security: "bearer"),
        new("POST", "/v1/api-keys", ["201", "400", "401", "403"], Security: "bearer"),
        new("DELETE", "/v1/api-keys/{id}", ["204", "401", "403", "404"], Security: "bearer"),
        new("GET", "/v1/profiles", ["200", "400", "401", "403"], Security: "bearer"),
        new("GET", "/v1/profiles/{id}", ["200", "401", "403", "404"], Security: "bearer"),
        new("GET", "/v1/profiles/{id}/events", ["200", "400", "401", "403", "404"], Security: "bearer"),
        new("POST", "/v1/track", ["202", "400", "401", "403", "413", "429", "503"], Security: "apiKey"),
        new("POST", "/v1/identify", ["202", "400", "401", "403", "413", "429", "503"], Security: "apiKey"),
        new("POST", "/v1/batch", ["202", "400", "401", "403", "413", "429", "503"], Security: "apiKey"),
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
            if (expected.Security is { } scheme)
            {
                hasSecurity.Should().BeTrue($"{expected.Method} {expected.Path} should require authentication");
                security.GetArrayLength().Should().Be(1);
                var requirement = security.EnumerateArray().Single();
                requirement.EnumerateObject().Select(p => p.Name).Should().Equal(scheme);
            }
            else
            {
                hasSecurity.Should().BeFalse($"{expected.Method} {expected.Path} should be anonymous");
            }

            foreach (var response in responses.EnumerateObject().Where(r => r.Name.StartsWith('4') || r.Name.StartsWith('5')))
            {
                response.Value.TryGetProperty("content", out var content)
                    .Should().BeTrue($"{expected.Method} {expected.Path} {response.Name} should have a body");
                content.EnumerateObject().Select(p => p.Name).Should()
                    .Equal(["application/problem+json"], $"{expected.Method} {expected.Path} {response.Name}");
            }
        }
    }

    [Fact]
    public void CT001_ingest_request_schemas_do_not_require_optional_fields()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "docs/api/openapi.json")));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        foreach (var name in new[] { "TrackRequest", "IdentifyRequest", "BatchItem", "BatchRequest" })
        {
            var schema = schemas.GetProperty(name);
            if (schema.TryGetProperty("required", out var required))
            {
                required.GetArrayLength().Should().Be(0, $"{name} has no required fields; every parameter is optional");
            }
        }

        schemas.GetProperty("BatchItemResult").GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).Should().Equal("index");
        schemas.GetProperty("IngestAccepted").GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).Should().Equal("eventId");
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

    private sealed record ExpectedOperation(string Method, string Path, string[] StatusCodes, string? Security);
}
