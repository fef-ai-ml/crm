namespace Crm.Segmentation.Domain.Rules;

/// <summary>A real-time membership transition for one profile in one segment (spec §5.4 step 4).</summary>
public enum Transition { None, Entered, Exited }

public static class Transitions
{
    /// <summary>UT-048: only a change of membership is a transition. The Redis store reports the same thing
    /// atomically (`SADD`/`SREM` returning 1, ruling I5); this is the rule it is mapped through.</summary>
    public static Transition From(bool previous, bool current) => (previous, current) switch
    {
        (false, true) => Transition.Entered,
        (true, false) => Transition.Exited,
        _ => Transition.None,
    };
}
