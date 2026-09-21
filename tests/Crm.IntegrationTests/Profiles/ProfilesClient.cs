using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Crm.IntegrationTests.Profiles;

internal static class ProfilesClient
{
    public static Task<HttpResponseMessage> GetAsync(this HttpClient client, string path, string bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client.SendAsync(request);
    }

    public static async Task<T> ReadAsync<T>(this HttpClient client, string path, string bearer)
    {
        using var response = await client.GetAsync(path, bearer);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
