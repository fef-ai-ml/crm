using System.Collections.Concurrent;
using System.Text.Json;
using Crm.Kernel.Time;
using Crm.Segmentation.Application.Realtime;
using Crm.Segmentation.Domain.Rules;
using Crm.Segmentation.Domain.Segments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crm.Segmentation.Infrastructure.Realtime;

/// <summary>A segment the real-time path evaluates: Active, eligible, its tree parsed once per cache load.</summary>
internal sealed record RealtimeSegment(Guid Id, RuleNode Tree, IReadOnlyList<EventNode> Leaves);

/// <summary>
/// Ruling I6 (spec §5.4 step 1): per tenant, the Active real-time-eligible segments, loaded with one query on
/// first use. An entry is dropped by <see cref="Invalidate"/> (a `segments:changed` message for the tenant) and
/// also expires after <see cref="RealtimeOptions.CacheTtl"/>, because pub/sub loses whatever is sent while the
/// Worker is reconnecting. A load in flight is shared by concurrent callers; a failed load is not cached.
/// </summary>
internal sealed class RealtimeSegmentCache(
    IServiceScopeFactory scopes,
    IClock clock,
    IOptions<RealtimeOptions> options,
    ILogger<RealtimeSegmentCache> logger)
{
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public async Task<IReadOnlyList<RealtimeSegment>> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var entry = _entries.AddOrUpdate(
            tenantId,
            _ => new Entry(LoadAsync(tenantId), now),
            (_, existing) => IsUsable(existing, now) ? existing : new Entry(LoadAsync(tenantId), now));

        try
        {
            return await entry.Segments.WaitAsync(cancellationToken);
        }
        catch (Exception) when (entry.Segments.IsFaulted)
        {
            _entries.TryRemove(new KeyValuePair<Guid, Entry>(tenantId, entry)); // the next call retries the load
            throw;
        }
    }

    /// <summary>A `segments:changed` message for <paramref name="tenantId"/> arrived; the next message reloads.</summary>
    public void Invalidate(Guid tenantId) => _entries.TryRemove(tenantId, out _);

    private bool IsUsable(Entry entry, DateTime now) => !entry.Segments.IsFaulted && now - entry.LoadedAt < options.Value.CacheTtl;

    private async Task<IReadOnlyList<RealtimeSegment>> LoadAsync(Guid tenantId)
    {
        await Task.Yield(); // AddOrUpdate may call the factory under contention; never run SQL inside it.

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SegmentationDbContext>();

        // Cross-tenant context (the Worker's scope has no tenant of its own), so filter by tenant explicitly.
        var rows = await db.Segments.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Status == SegmentStatus.Active && s.IsRealtimeEligible)
            .Select(s => new { s.Id, s.Definition })
            .ToListAsync();

        var segments = new List<RealtimeSegment>(rows.Count);
        foreach (var row in rows)
        {
            if (Parse(row.Id, row.Definition) is { } tree)
            {
                segments.Add(new RealtimeSegment(row.Id, tree, RealtimeCounterKey.Leaves(tree)));
            }
        }

        return segments;
    }

    // I6: a stored definition that no longer parses, or no longer classifies as eligible, is skipped — it must
    // never fail every message of the tenant.
    private RuleNode? Parse(Guid segmentId, string definition)
    {
        try
        {
            using var document = JsonDocument.Parse(definition);
            var parsed = RuleParser.Parse(document.RootElement);
            if (parsed.IsValid && RealtimeEligibility.Classify(parsed.Tree!).IsEligible)
            {
                return parsed.Tree;
            }
        }
        catch (JsonException)
        {
            // falls through to the warning below
        }

        logger.LogWarning("Segment {SegmentId} is flagged real-time eligible but its definition does not parse as an eligible tree; skipped", segmentId);
        return null;
    }

    private sealed record Entry(Task<IReadOnlyList<RealtimeSegment>> Segments, DateTime LoadedAt);
}
