using Crm.IntegrationTests.Fixtures;
using Crm.Profiles.Application.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Crm.IntegrationTests.Profiles;

/// <summary>
/// Ruling E15: the handler is called directly. An identify for u1 and tracks for u1 share a Kafka partition, so the
/// broker cannot produce this interleaving on demand; what is proven is the row-lock protocol of spec §4.2.
/// </summary>
[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class MergeConcurrencyTests(CrmStackFixture stack)
{
    private const int ConcurrentTracks = 50;

    [Fact]
    public async Task IT008_events_arriving_during_a_merge_are_not_lost()
    {
        var tenantId = Guid.CreateVersion7(); // schema profiles has no foreign key to identity.Tenants
        var handler = stack.Worker.Services.GetRequiredService<IRawMessageHandler>();
        var t0 = DateTime.UtcNow.AddMinutes(-10);

        var p1 = (await handler.HandleAsync(RawEnvelopes.Track(tenantId, null, "a1", t0), CancellationToken.None)).ProfileId;
        var p2 = (await handler.HandleAsync(RawEnvelopes.Track(tenantId, "u1", null, t0.AddMinutes(1)), CancellationToken.None)).ProfileId;

        var locksTaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hold = stack.Hooks.OnMergeLocksTaken(async (tenant, _) =>
        {
            if (tenant == tenantId)
            {
                locksTaken.TrySetResult();
                await release.Task;
            }
        });

        var merge = Task.Run(() => handler.HandleAsync(RawEnvelopes.Identify(tenantId, "u1", "a1", """{"plan":"pro"}""", t0.AddMinutes(2)), CancellationToken.None));
        await locksTaken.Task.WaitAsync(TimeSpan.FromSeconds(60));

        // Both rows are locked and nothing is changed yet. These resolve u1 → P2 (the future loser) and queue on its lock.
        var tracks = Enumerable.Range(0, ConcurrentTracks)
            .Select(i => Task.Run(() => handler.HandleAsync(RawEnvelopes.Track(tenantId, "u1", null, t0.AddMinutes(3).AddSeconds(i)), CancellationToken.None)))
            .ToList();
        await Task.Delay(TimeSpan.FromSeconds(2));
        tracks.Should().OnlyContain(t => !t.IsCompleted, "every applier waits for the merge's row locks");

        release.SetResult();
        var mergeOutcome = await merge.WaitAsync(TimeSpan.FromSeconds(120));
        var outcomes = await Task.WhenAll(tracks).WaitAsync(TimeSpan.FromSeconds(120));

        mergeOutcome.Merged.Should().BeTrue();
        mergeOutcome.ProfileId.Should().Be(p1, "the anonymous profile is older");
        outcomes.Should().OnlyContain(o => o.ProfileId == p1, "an event that raced the merge is applied to the winner");
        outcomes.Should().Contain(o => o.Redirected, "they resolved to P2 before the merge committed and followed MergedIntoId");

        var events = await ProfilesDb.EventsAsync(stack.SqlConnectionString, tenantId);
        events.Should().HaveCount(2 + ConcurrentTracks);
        events.Should().OnlyContain(e => e.ProfileId == p1, "no event references the loser");
        (await ProfilesDb.GetAsync(stack.SqlConnectionString, tenantId, p1))!.EventCount.Should().Be(2 + ConcurrentTracks);
        (await ProfilesDb.GetAsync(stack.SqlConnectionString, tenantId, p2))!.MergedIntoId.Should().Be(p1);
    }
}
