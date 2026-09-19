using System.Net.Http.Json;
using Crm.Tenancy.Infrastructure.Auth;

namespace Crm.IntegrationTests.Ingestion;

internal static class IngestClient
{
    public static Task<HttpResponseMessage> PostWithKeyAsync(this HttpClient client, string path, string apiKey, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add(AuthSchemes.ApiKeyHeader, apiKey);
        return client.SendAsync(request);
    }
}
