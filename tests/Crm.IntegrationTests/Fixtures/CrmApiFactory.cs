using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>Hosts Crm.Api in-process against the fixture's containers. Environment "Testing" so Development-only endpoints stay off unless a test opts in.</summary>
public sealed class CrmApiFactory : WebApplicationFactory<Crm.Api.Program>
{
    public const string SigningKey = "integration-test-signing-key-0123456789abcdef0123";

    private readonly CrmStackFixture _stack;

    /// <summary>
    /// Crm.Api.Program reads CrmHostingOptions from configuration eagerly, before WebApplicationBuilder.Build()
    /// runs. WebApplicationFactory's ConfigureAppConfiguration hook only applies during that Build() call, which
    /// is too late for an eager read, so the stack's connection settings are also pushed in as process
    /// environment variables here, before CreateClient() triggers the entry point's Main.
    /// </summary>
    public CrmApiFactory(CrmStackFixture stack)
    {
        _stack = stack;
        Environment.SetEnvironmentVariable("ConnectionStrings__Crm", stack.SqlConnectionString);
        Environment.SetEnvironmentVariable("Redis__Configuration", stack.RedisConfiguration);
        Environment.SetEnvironmentVariable("Kafka__BootstrapServers", stack.KafkaBootstrapServers);
        Environment.SetEnvironmentVariable("Auth__SigningKey", SigningKey);
    }

    public string EnvironmentName { get; init; } = "Testing";

    /// <summary>Cookies are asserted and replayed by hand: the refresh cookie is Secure and the test server speaks plain HTTP.</summary>
    public HttpClient CreateApiClient() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Crm"] = _stack.SqlConnectionString,
            ["Redis:Configuration"] = _stack.RedisConfiguration,
            ["Kafka:BootstrapServers"] = _stack.KafkaBootstrapServers,
            ["Auth:SigningKey"] = SigningKey,
        }));
    }
}
