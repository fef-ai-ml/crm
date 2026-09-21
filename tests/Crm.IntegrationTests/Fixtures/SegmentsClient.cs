using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>Authenticated JSON write helpers for the segments endpoints. GET reuses ProfilesClient's GetAsync/ReadAsync.</summary>
internal static class SegmentsClient
{
    public static Task<HttpResponseMessage> PostAsync(this HttpClient client, string path, string bearer, object body)
        => SendAsync(client, HttpMethod.Post, path, bearer, body);

    public static Task<HttpResponseMessage> PutAsync(this HttpClient client, string path, string bearer, object body)
        => SendAsync(client, HttpMethod.Put, path, bearer, body);

    public static Task<HttpResponseMessage> DeleteAsync(this HttpClient client, string path, string bearer)
        => SendAsync(client, HttpMethod.Delete, path, bearer, null);

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string bearer, object? body)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return client.SendAsync(request);
    }
}
