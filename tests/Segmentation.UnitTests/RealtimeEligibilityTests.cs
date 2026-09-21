using Crm.Segmentation.Domain.Rules;
using FluentAssertions;
using Xunit;

namespace Crm.Segmentation.UnitTests;

public class RealtimeEligibilityTests
{
    private static AttributeNode Attribute(string key, AttributeCmp cmp, RuleValue? value = null) => new(key, cmp, value);

    [Fact]
    public void UT040_attribute_only_tree_using_every_comparator_is_eligible()
    {
        var tree = new GroupNode(GroupOp.And,
        [
            Attribute("plan", AttributeCmp.Eq, new RuleValue.Text("pro")),
            Attribute("plan", AttributeCmp.Neq, new RuleValue.Text("free")),
            new GroupNode(GroupOp.Or,
            [
                Attribute("country", AttributeCmp.In, new RuleValue.TextList(["ID", "SG"])),
                Attribute("country", AttributeCmp.Nin, new RuleValue.TextList(["US"])),
                Attribute("ltv", AttributeCmp.Gt, new RuleValue.Number(10)),
                Attribute("ltv", AttributeCmp.Gte, new RuleValue.Number(10)),
                Attribute("ltv", AttributeCmp.Lt, new RuleValue.Number(10)),
                Attribute("ltv", AttributeCmp.Lte, new RuleValue.Number(10)),
                Attribute("campaign", AttributeCmp.Contains, new RuleValue.Text("off")),
                Attribute("tier", AttributeCmp.Exists),
                Attribute("tier", AttributeCmp.NotExists),
            ]),
        ]);

        RealtimeEligibility.Classify(tree).Should().Be(new EligibilityResult(true, null));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void UT041_event_gte_with_count_at_least_one_is_eligible(int count)
    {
        var tree = new EventNode("purchase", EventCmp.Gte, count, new RuleWindow(30, 'd'));

        RealtimeEligibility.Classify(tree).Should().Be(new EligibilityResult(true, null));
    }

    [Fact]
    public void UT042_not_group_is_ineligible()
    {
        var tree = new GroupNode(GroupOp.Not, [Attribute("plan", AttributeCmp.Eq, new RuleValue.Text("pro"))]);

        RealtimeEligibility.Classify(tree).Should().Be(new EligibilityResult(false, IneligibilityReason.NotGroup));
    }

    [Fact]
    public void UT043_event_eq_zero_is_ineligible()
    {
        var tree = new EventNode("app_open", EventCmp.Eq, 0, new RuleWindow(7, 'd'));

        RealtimeEligibility.Classify(tree).Should().Be(new EligibilityResult(false, IneligibilityReason.ZeroCount));
    }

    [Fact]
    public void UT044_event_lte_is_ineligible()
    {
        var tree = new EventNode("purchase", EventCmp.Lte, 2, new RuleWindow(30, 'd'));

        RealtimeEligibility.Classify(tree).Should().Be(new EligibilityResult(false, IneligibilityReason.UpperBound));
    }

    [Fact]
    public void UT045_event_eq_n_at_least_one_is_ineligible()
    {
        var tree = new EventNode("purchase", EventCmp.Eq, 2, new RuleWindow(30, 'd'));

        RealtimeEligibility.Classify(tree).Should().Be(new EligibilityResult(false, IneligibilityReason.ExactCount));
    }

    [Fact]
    public void event_gte_with_zero_count_is_zero_count_not_upper_bound()
    {
        var tree = new EventNode("app_open", EventCmp.Gte, 0, new RuleWindow(7, 'd'));

        RealtimeEligibility.Classify(tree).Should().Be(new EligibilityResult(false, IneligibilityReason.ZeroCount));
    }

    [Fact]
    public void the_first_reason_in_pre_order_wins()
    {
        var tree = new GroupNode(GroupOp.And,
        [
            new EventNode("app_open", EventCmp.Eq, 0, new RuleWindow(7, 'd')),
            new GroupNode(GroupOp.Not, [Attribute("plan", AttributeCmp.Eq, new RuleValue.Text("pro"))]),
        ]);

        RealtimeEligibility.Classify(tree).Should().Be(new EligibilityResult(false, IneligibilityReason.ZeroCount));
    }

    [Fact]
    public void a_not_group_nested_under_and_or_is_still_found()
    {
        var tree = new GroupNode(GroupOp.And,
        [
            Attribute("plan", AttributeCmp.Eq, new RuleValue.Text("pro")),
            new GroupNode(GroupOp.Not, [Attribute("tier", AttributeCmp.Eq, new RuleValue.Text("gold"))]),
        ]);

        RealtimeEligibility.Classify(tree).Should().Be(new EligibilityResult(false, IneligibilityReason.NotGroup));
    }
}
