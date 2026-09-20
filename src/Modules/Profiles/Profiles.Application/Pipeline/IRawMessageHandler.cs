using Crm.Profiles.Application.Messages;

namespace Crm.Profiles.Application.Pipeline;

/// <summary>Decode → tenant scope → <see cref="ProfilePipeline"/> → commit, for one consumed record. One call is one attempt.</summary>
public interface IRawMessageHandler
{
    Task<ProcessingOutcome> HandleAsync(RawEnvelope envelope, CancellationToken cancellationToken);
}
