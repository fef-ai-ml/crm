namespace Crm.Profiles.Application.Messages;

/// <param name="Messages">To publish after the SQL commit, in order.</param>
/// <param name="ProfileId">The live profile the message was applied to.</param>
/// <param name="Duplicate">The event already existed; nothing was written (FR-06). EventTracked is still in <paramref name="Messages"/> (ruling E7).</param>
/// <param name="Redirected">Resolution started at a merge loser and followed MergedIntoId.</param>
/// <param name="Merged">An identify call merged a second profile into <paramref name="ProfileId"/>.</param>
public sealed record ProcessingOutcome(IReadOnlyList<ProcessedMessage> Messages, Guid ProfileId, bool Duplicate, bool Redirected, bool Merged);
