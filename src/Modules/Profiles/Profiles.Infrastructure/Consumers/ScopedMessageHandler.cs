using Crm.Kernel.Tenancy;
using Crm.Profiles.Application.Messages;
using Crm.Profiles.Application.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Crm.Profiles.Infrastructure.Consumers;

/// <summary>Spec §2.5: consumers create a tenant scope per message. One call = one attempt = one DI scope = one SQL transaction.</summary>
internal sealed class ScopedMessageHandler(IServiceScopeFactory scopes, ILogger<ScopedMessageHandler> logger) : IRawMessageHandler
{
    public async Task<ProcessingOutcome> HandleAsync(RawEnvelope envelope, CancellationToken cancellationToken)
    {
        var message = RawMessageDecoder.Decode(envelope);

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(message.TenantId, null, TenantPrincipalKind.None);
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["TenantId"] = message.TenantId });

        var outcome = await scope.ServiceProvider.GetRequiredService<ProfilePipeline>().HandleAsync(message, cancellationToken);

        if (outcome.Redirected && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "MergedIntoId redirect: {Topic}[{Partition}]@{Offset} resolved to a merge loser and was applied to winner {ProfileId}",
                envelope.Topic, envelope.Partition, envelope.Offset, outcome.ProfileId);
        }

        return outcome;
    }
}
