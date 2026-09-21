namespace Crm.Segmentation.Domain.Rules;

/// <summary>Why a rule tree cannot be evaluated in real time (ADR-0005 §5.4); the batch path still applies.</summary>
public enum IneligibilityReason { NotGroup, ZeroCount, UpperBound, ExactCount }

/// <summary>Result of <see cref="RealtimeEligibility.Classify"/>.</summary>
public readonly record struct EligibilityResult(bool IsEligible, IneligibilityReason? Reason);

/// <summary>Classifies whether a rule tree can be evaluated in real time from a profile snapshot plus event
/// counters, with no need to know about absent data (ADR-0005 §5.4): no `not` group, every attribute leaf (any
/// comparator), every event leaf with `cmp ∈ gte` and `count ≥ 1`.</summary>
public static class RealtimeEligibility
{
    public static EligibilityResult Classify(RuleNode tree)
    {
        var reason = FirstReason(tree);
        return reason is null ? new EligibilityResult(true, null) : new EligibilityResult(false, reason);
    }

    // Pre-order walk; the first reason found wins.
    private static IneligibilityReason? FirstReason(RuleNode node) => node switch
    {
        GroupNode { Op: GroupOp.Not } => IneligibilityReason.NotGroup,
        GroupNode group => group.Children.Select(FirstReason).FirstOrDefault(reason => reason is not null),
        EventNode { Cmp: EventCmp.Eq, Count: 0 } => IneligibilityReason.ZeroCount,
        EventNode { Cmp: EventCmp.Gte, Count: 0 } => IneligibilityReason.ZeroCount,
        EventNode { Cmp: EventCmp.Lte } => IneligibilityReason.UpperBound,
        EventNode { Cmp: EventCmp.Eq } => IneligibilityReason.ExactCount, // Count >= 1 here; 0 is handled above.
        _ => null, // AttributeNode and event gte with count >= 1 are always eligible.
    };
}
