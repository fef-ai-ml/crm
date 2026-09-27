using Crm.Profiles.Application.Pipeline;
using Crm.Segmentation.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>
/// Hosts Crm.Worker in-process (docs/test/integration.md: "the Worker as a hosted service in the same test host").
/// One instance serves the whole run (ruling E16): it consumes group `profiles` like production, with short retry
/// and restart delays, and with <see cref="SwitchableHooks"/> in place of the no-op hooks.
/// </summary>
public sealed class CrmWorkerFactory : WebApplicationFactory<Crm.Worker.Program>
{
    private readonly CrmStackFixture _stack;
    private readonly SwitchableHooks _hooks;

    public CrmWorkerFactory(CrmStackFixture stack, SwitchableHooks hooks)
    {
        _stack = stack;
        _hooks = hooks;

        // Crm.Worker.Program reads connection settings while building, before ConfigureAppConfiguration applies (see CrmApiFactory).
        Environment.SetEnvironmentVariable("ConnectionStrings__Crm", stack.SqlConnectionString);
        Environment.SetEnvironmentVariable("Redis__Configuration", stack.RedisConfiguration);
        Environment.SetEnvironmentVariable("Kafka__BootstrapServers", stack.KafkaBootstrapServers);
    }

    /// <summary>Builds and starts the host (hosted services included).</summary>
    public void Start() => _ = Services;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Crm"] = _stack.SqlConnectionString,
            ["Redis:Configuration"] = _stack.RedisConfiguration,
            ["Kafka:BootstrapServers"] = _stack.KafkaBootstrapServers,
            ["Profiles:Consumer:RetryDelaysMs:0"] = "50",
            ["Profiles:Consumer:RetryDelaysMs:1"] = "100",
            ["Profiles:Consumer:RestartDelayMs"] = "200",
            // Default is 5 s (SegmentRunOptions.PollInterval); the full suite polling that slowly is most of why
            // it used to take 11m44s (pre-step, plan H task 8).
            ["Segmentation:Run:PollInterval"] = "00:00:00.250",
            ["Segmentation:Realtime:RetryDelaysMs:0"] = "50",
            ["Segmentation:Realtime:RetryDelaysMs:1"] = "100",
            ["Segmentation:Realtime:RestartDelayMs"] = "200",
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPipelineHooks>();
            services.AddSingleton<IPipelineHooks>(_hooks);
            services.RemoveAll<ISegmentationHooks>();
            services.AddSingleton<ISegmentationHooks>(_stack.SegmentHooks);
        });
    }
}
