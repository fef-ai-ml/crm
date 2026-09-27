using Crm.Kernel.Redis;
using Crm.Segmentation.Domain.Rules;

namespace Crm.Segmentation.Application.Realtime;

/// <summary>A real-time event counter (spec §3.4): one key per (segment, event name, window, profile), living for
/// the leaf's window (ruling I3).</summary>
public readonly record struct RealtimeCounterKey(string Key, TimeSpan Ttl)
{
    public static RealtimeCounterKey For(Guid tenantId, Guid segmentId, EventNode leaf, Guid profileId)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        return new RealtimeCounterKey(RedisKeys.EventCounter(tenantId, segmentId, leaf.Name, leaf.Window.Text, profileId), leaf.Window.Duration);
    }

    /// <summary>The event leaves of <paramref name="tree"/>, one per distinct (name, window) in pre-order: two
    /// leaves that share both read and increment one counter, so an event is counted once per key (I3).</summary>
    public static IReadOnlyList<EventNode> Leaves(RuleNode tree)
    {
        var leaves = new List<EventNode>();
        var seen = new HashSet<(string, RuleWindow)>();
        Collect(tree, leaves, seen);
        return leaves;
    }

    private static void Collect(RuleNode node, List<EventNode> leaves, HashSet<(string, RuleWindow)> seen)
    {
        switch (node)
        {
            case GroupNode group:
                foreach (var child in group.Children)
                {
                    Collect(child, leaves, seen);
                }

                break;
            case EventNode leaf when seen.Add((leaf.Name, leaf.Window)):
                leaves.Add(leaf);
                break;
        }
    }
}
