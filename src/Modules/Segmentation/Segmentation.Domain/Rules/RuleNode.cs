namespace Crm.Segmentation.Domain.Rules;

/// <summary>Boolean group operators (spec §5.1). `Not` groups have exactly one child.</summary>
public enum GroupOp { And, Or, Not }

/// <summary>Attribute leaf comparators. JSON: eq neq in nin gt gte lt lte contains exists not_exists.</summary>
public enum AttributeCmp { Eq, Neq, In, Nin, Gt, Gte, Lt, Lte, Contains, Exists, NotExists }

/// <summary>Event leaf comparators. JSON: eq gte lte.</summary>
public enum EventCmp { Eq, Gte, Lte }

/// <summary>A node in a segment rule tree (spec §5.1): a boolean group or an attribute/event leaf.</summary>
public abstract record RuleNode;

/// <summary>A boolean group of child nodes. <see cref="Children"/> compares by sequence, not by reference.</summary>
public sealed record GroupNode(GroupOp Op, IReadOnlyList<RuleNode> Children) : RuleNode
{
    public bool Equals(GroupNode? other) => other is not null && Op == other.Op && Children.SequenceEqual(other.Children);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Op);
        foreach (var child in Children)
        {
            hash.Add(child);
        }

        return hash.ToHashCode();
    }
}

/// <summary>An attribute leaf. <see cref="Value"/> is null for <see cref="AttributeCmp.Exists"/> and <see cref="AttributeCmp.NotExists"/>.</summary>
public sealed record AttributeNode(string Key, AttributeCmp Cmp, RuleValue? Value) : RuleNode;

/// <summary>An event leaf: has the profile's <see cref="Name"/> event occurred <see cref="Cmp"/> <see cref="Count"/> times in <see cref="Window"/>.</summary>
public sealed record EventNode(string Name, EventCmp Cmp, int Count, RuleWindow Window) : RuleNode;

/// <summary>An attribute leaf's typed value. The JSON kind (string/number/bool/array) selects the variant (H17).</summary>
public abstract record RuleValue
{
    public sealed record Text(string Value) : RuleValue;

    public sealed record Number(double Value) : RuleValue;

    public sealed record Bool(bool Value) : RuleValue;

    /// <summary><see cref="Values"/> compares by sequence, not by reference.</summary>
    public sealed record TextList(IReadOnlyList<string> Values) : RuleValue
    {
        public bool Equals(TextList? other) => other is not null && Values.SequenceEqual(other.Values, StringComparer.Ordinal);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var value in Values)
            {
                hash.Add(value, StringComparer.Ordinal);
            }

            return hash.ToHashCode();
        }
    }

    /// <summary><see cref="Values"/> compares by sequence, not by reference.</summary>
    public sealed record NumberList(IReadOnlyList<double> Values) : RuleValue
    {
        public bool Equals(NumberList? other) => other is not null && Values.SequenceEqual(other.Values);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var value in Values)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }
}

/// <summary>An event leaf's lookback window: <see cref="Amount"/> days or hours (<see cref="Unit"/> is 'd' or 'h').</summary>
public readonly record struct RuleWindow(int Amount, char Unit)
{
    /// <summary>The `Nd` / `Nh` form stored in the canonical definition (H20).</summary>
    public string Text => $"{Amount}{Unit}";

    public TimeSpan Duration => Unit == 'd' ? TimeSpan.FromDays(Amount) : TimeSpan.FromHours(Amount);
}
