using Crm.Segmentation.Application.Realtime;
using Crm.Segmentation.Domain.Rules;
using FluentAssertions;
using Xunit;

namespace Crm.Segmentation.UnitTests;

public class RedisKeyBuilderTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Segment = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Profile = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const string T = "11111111111111111111111111111111";
    private const string S = "22222222222222222222222222222222";
    private const string P = "33333333333333333333333333333333";

    [Fact]
    public void UT049_counter_key_includes_event_name_and_window()
    {
        var thirty = new EventNode("purchase", EventCmp.Gte, 1, new RuleWindow(30, 'd'));
        var seven = new EventNode("purchase", EventCmp.Gte, 1, new RuleWindow(7, 'd'));

        var a = RealtimeCounterKey.For(Tenant, Segment, thirty, Profile);
        var b = RealtimeCounterKey.For(Tenant, Segment, seven, Profile);

        a.Key.Should().Be($"t:{T}:seg:{S}:ev:purchase:30d:{P}");
        a.Ttl.Should().Be(TimeSpan.FromDays(30));
        b.Key.Should().Be($"t:{T}:seg:{S}:ev:purchase:7d:{P}");
        b.Ttl.Should().Be(TimeSpan.FromDays(7));
    }

    [Fact]
    public void Hour_windows_keep_their_unit()
    {
        var leaf = new EventNode("app_open", EventCmp.Gte, 1, new RuleWindow(12, 'h'));

        var key = RealtimeCounterKey.For(Tenant, Segment, leaf, Profile);

        key.Key.Should().Be($"t:{T}:seg:{S}:ev:app_open:12h:{P}");
        key.Ttl.Should().Be(TimeSpan.FromHours(12));
    }

    [Fact]
    public void I3_leaves_of_one_tree_collapse_to_distinct_name_and_window_pairs()
    {
        var tree = new GroupNode(GroupOp.Or,
        [
            new EventNode("purchase", EventCmp.Gte, 1, new RuleWindow(30, 'd')),
            new GroupNode(GroupOp.And,
            [
                new EventNode("purchase", EventCmp.Gte, 3, new RuleWindow(30, 'd')),
                new EventNode("purchase", EventCmp.Gte, 1, new RuleWindow(7, 'd')),
                new AttributeNode("plan", AttributeCmp.Exists, null),
            ]),
            new EventNode("app_open", EventCmp.Gte, 1, new RuleWindow(30, 'd')),
        ]);

        RealtimeCounterKey.Leaves(tree).Select(l => (l.Name, l.Window.Text)).Should().Equal(
            ("purchase", "30d"), ("purchase", "7d"), ("app_open", "30d"));
    }
}
