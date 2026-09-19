using Crm.Ingestion.Application.Validation;
using Crm.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Crm.IntegrationTests.Ingestion;

/// <summary>I1: TestServer does not enforce Kestrel's request size limit, so pin the declared metadata directly.</summary>
[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class IngestEndpointMetadataTests(CrmStackFixture stack) : IAsyncLifetime
{
    private CrmApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new CrmApiFactory(stack);
        using var warmup = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Theory]
    [InlineData("/v1/track", IngestRules.MaxSingleRequestBytes)]
    [InlineData("/v1/identify", IngestRules.MaxSingleRequestBytes)]
    [InlineData("/v1/batch", IngestRules.MaxBatchRequestBytes)]
    public void Ingest_routes_declare_their_request_size_limit(string route, long expectedBytes)
    {
        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var endpoint = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == route);

        var metadata = endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>();

        metadata.Should().NotBeNull($"{route} should declare a request size limit");
        metadata!.MaxRequestBodySize.Should().Be(expectedBytes);
    }
}
