using Crm.Profiles.Application.Messages;

namespace Crm.Profiles.Application.Pipeline;

/// <summary>
/// Seams for the two orderings the integration tests must prove (IT-006, IT-008). Production uses
/// <see cref="NoPipelineHooks"/>. Both calls carry enough context for a test to react to its own tenant only.
/// </summary>
public interface IPipelineHooks
{
    /// <summary>Both profile rows of a merge are locked; nothing has been changed yet.</summary>
    Task MergeLocksTakenAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>The SQL transaction for this record committed; nothing was published and the Kafka offset is not committed yet.</summary>
    Task SqlCommittedAsync(RawEnvelope envelope, CancellationToken cancellationToken);
}

public sealed class NoPipelineHooks : IPipelineHooks
{
    public Task MergeLocksTakenAsync(Guid tenantId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SqlCommittedAsync(RawEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
}
