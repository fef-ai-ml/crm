using System.Diagnostics;
using System.Text.Json;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Segmentation.Application.Realtime;
using Crm.Segmentation.Domain.Rules;
using Crm.Segmentation.Domain.Segments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Crm.Segmentation.Infrastructure.Realtime;

/// <summary>
/// One `events.processed` record through the real-time path (spec §5.4, rulings I3–I9), in its own DI scope and
/// tenant context. Never throws for a bad message, a Redis outage or a SQL failure — each is logged, counted in
/// <c>crm_realtime_skipped_total{reason}</c> and skipped (I9), so one partition never stalls behind it; only
/// cancellation escapes. Resolved per message by <see cref="ProcessedEventsConsumerService"/>; integration tests
/// resolve it the same way to drive one message directly (I14).
/// </summary>
internal sealed class RealtimeMessageHandler(
    TenantContext tenant,
    SegmentationDbContext db,
    RealtimeSegmentCache cache,
    RealtimeMembershipStore store,
    SegmentationMetrics metrics,
    IClock clock,
    IOptions<RealtimeOptions> options,
    ILogger<RealtimeMessageHandler> logger)
{
    public async Task HandleAsync(ProcessedEnvelope envelope, CancellationToken cancellationToken)
    {
        RealtimeMessage? message;
        try
        {
            message = RealtimeMessageDecoder.Decode(envelope);
        }
        catch (RealtimeDecodeException ex)
        {
            logger.LogError(ex, "Skipping undecodable {Topic}[{Partition}]@{Offset}", envelope.Topic, envelope.Partition, envelope.Offset);
            metrics.RealtimeSkipped("decode");
            return;
        }

        if (message is null)
        {
            return; // a type the real-time path does not act on
        }

        tenant.Resolve(message.TenantId, null, TenantPrincipalKind.None);
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["TenantId"] = message.TenantId });
        var started = Stopwatch.GetTimestamp();

        IReadOnlyList<RealtimeSegment> segments;
        try
        {
            segments = await WithRetriesAsync(() => cache.GetAsync(message.TenantId, cancellationToken), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Loading the real-time segments of tenant {TenantId} failed; skipping {Topic}[{Partition}]@{Offset}", message.TenantId, envelope.Topic, envelope.Partition, envelope.Offset);
            metrics.RealtimeSkipped("sql");
            return;
        }

        if (segments.Count == 0)
        {
            return;
        }

        var now = clock.UtcNow;
        var changes = new List<PendingChange>();
        var redisFailed = false;
        try
        {
            await ApplyAsync(message, segments, now, changes);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // I9: real-time state is approximate and rebuildable; the transitions already applied are still
            // recorded below, the rest of this message is left for the next event or the next batch run.
            logger.LogWarning(ex, "Redis failed while evaluating {Topic}[{Partition}]@{Offset}; skipping the rest of it", envelope.Topic, envelope.Partition, envelope.Offset);
            redisFailed = true;
        }

        if (changes.Count > 0 && !await SaveAsync(changes, now, cancellationToken))
        {
            metrics.RealtimeSkipped("sql");
            return;
        }

        if (redisFailed)
        {
            metrics.RealtimeSkipped("redis");
            return;
        }

        metrics.RecordRealtimeEval(Stopwatch.GetElapsedTime(started).TotalSeconds, message);
    }

    private async Task ApplyAsync(RealtimeMessage message, IReadOnlyList<RealtimeSegment> segments, DateTime now, List<PendingChange> changes)
    {
        var tenantId = message.TenantId;
        switch (message)
        {
            case RealtimeMessage.Tracked { Message: var tracked }:
                foreach (var segment in segments)
                {
                    var counts = await store.CountAsync(tenantId, segment.Id, segment.Leaves, tracked.ProfileId, tracked.Event, tracked.OccurredAt, now);
                    await EvaluateAsync(tenantId, segment, tracked.ProfileId, tracked.Profile?.Attributes ?? default, counts, changes);
                }

                break;

            case RealtimeMessage.Updated { Message: var updated }:
                foreach (var segment in segments)
                {
                    var counts = await store.CountAsync(tenantId, segment.Id, segment.Leaves, updated.ProfileId, eventName: null, updated.OccurredAt, now);
                    await EvaluateAsync(tenantId, segment, updated.ProfileId, updated.Profile?.Attributes ?? default, counts, changes);
                }

                break;

            case RealtimeMessage.Merged { Message: var merged }:
                // I8: the loser leaves every set; the winner is re-evaluated by the message that follows the merge.
                foreach (var segment in segments)
                {
                    if (await store.RemoveAsync(tenantId, segment.Id, merged.LoserProfileId))
                    {
                        changes.Add(new PendingChange(segment.Id, merged.LoserProfileId, Transition.Exited));
                    }
                }

                break;
        }
    }

    private async Task EvaluateAsync(
        Guid tenantId, RealtimeSegment segment, Guid profileId, JsonElement attributes,
        IReadOnlyDictionary<(string Name, RuleWindow Window), long> counts, List<PendingChange> changes)
    {
        var member = RealtimeEvaluator.Evaluate(segment.Tree, attributes, leaf => counts.GetValueOrDefault((leaf.Name, leaf.Window)));

        // I5: the set mutation is the transition detector — SADD/SREM report whether membership actually changed.
        var changed = member
            ? await store.AddAsync(tenantId, segment.Id, profileId)
            : await store.RemoveAsync(tenantId, segment.Id, profileId);
        var transition = Transitions.From(previous: member ^ changed, current: member);
        if (transition != Transition.None)
        {
            changes.Add(new PendingChange(segment.Id, profileId, transition));
        }
    }

    /// <summary>I5/I9: one SaveChanges for the message's change rows, retried; if it still fails, the set
    /// mutations behind those rows are reverted so the next message for the profile re-detects them.</summary>
    private async Task<bool> SaveAsync(List<PendingChange> changes, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            await WithRetriesAsync(
                async () =>
                {
                    db.ChangeTracker.Clear();
                    db.MembershipChanges.AddRange(changes.Select(c => MembershipChange.Record(
                        tenant.TenantId, c.SegmentId, c.ProfileId,
                        c.Transition == Transition.Entered ? MembershipChangeKind.Entered : MembershipChangeKind.Exited,
                        MembershipChangeSource.Realtime, now)));
                    return await db.SaveChangesAsync(cancellationToken);
                },
                cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Recording {Count} real-time membership changes failed; reverting their set updates", changes.Count);
            await RevertAsync(changes);
            return false;
        }
    }

    private async Task RevertAsync(List<PendingChange> changes)
    {
        foreach (var change in changes)
        {
            try
            {
                _ = change.Transition == Transition.Entered
                    ? await store.RemoveAsync(tenant.TenantId, change.SegmentId, change.ProfileId)
                    : await store.AddAsync(tenant.TenantId, change.SegmentId, change.ProfileId);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                logger.LogError(ex, "Reverting the {Transition} of profile {ProfileId} in segment {SegmentId} failed; the next batch run corrects the set", change.Transition, change.ProfileId, change.SegmentId);
            }
        }
    }

    private async Task<T> WithRetriesAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var delays = options.Value.RetryDelays;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception) when (attempt < delays.Count && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(delays[attempt], cancellationToken);
            }
        }
    }

    private sealed record PendingChange(Guid SegmentId, Guid ProfileId, Transition Transition);
}
