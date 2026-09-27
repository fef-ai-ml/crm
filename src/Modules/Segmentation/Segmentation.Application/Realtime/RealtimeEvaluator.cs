using System.Globalization;
using System.Text.Json;
using Crm.Segmentation.Application.Compilation;
using Crm.Segmentation.Domain.Rules;

namespace Crm.Segmentation.Application.Realtime;

/// <summary>
/// The real-time twin of <see cref="RuleCompiler"/> (spec §5.4 step 3): evaluates an eligible rule tree against a
/// profile's attribute snapshot plus event counters. Ruling I1: it reproduces what the compiled SQL answers, not
/// a C#-native reading of the rule — an attribute is what `JSON_VALUE` returns, text compares case-insensitively
/// with trailing spaces ignored (the database's `_CI_` collation and `=` padding), numbers go through the same
/// conversion as `TRY_CONVERT(float, …)`, and missing-key semantics follow docs/design/architecture.md §6 — so a
/// batch run does not flip members the real-time path just admitted. IT-043 holds the two paths to one answer.
/// </summary>
public static class RealtimeEvaluator
{
    /// <summary>`JSON_VALUE` in lax mode returns NULL for a string longer than this.</summary>
    private const int JsonValueMaxLength = 4000;

    /// <summary>Promoted keys are `CAST(JSON_VALUE(...) AS nvarchar(256))` computed columns.</summary>
    private const int PromotedColumnLength = 256;

    /// <summary><paramref name="counter"/> returns the current count for an event leaf (0 when absent). Throws
    /// <see cref="ArgumentException"/> for a node real-time evaluation cannot answer (a `not` group, or an event
    /// leaf other than `gte n ≥ 1`) — callers pass only trees <see cref="RealtimeEligibility"/> accepted.</summary>
    public static bool Evaluate(RuleNode tree, JsonElement attributes, Func<EventNode, long> counter)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(counter);
        return EvaluateNode(tree, attributes, counter);
    }

    private static bool EvaluateNode(RuleNode node, JsonElement attributes, Func<EventNode, long> counter) => node switch
    {
        GroupNode { Op: GroupOp.And } group => group.Children.All(child => EvaluateNode(child, attributes, counter)),
        GroupNode { Op: GroupOp.Or } group => group.Children.Any(child => EvaluateNode(child, attributes, counter)),
        GroupNode { Op: GroupOp.Not } => throw new ArgumentException("A 'not' group cannot be evaluated in real time.", nameof(node)),
        AttributeNode attribute => EvaluateAttribute(attribute, Resolve(attributes, attribute.Key)),
        EventNode { Cmp: EventCmp.Gte, Count: >= 1 } @event => counter(@event) >= @event.Count,
        EventNode @event => throw new ArgumentException($"Event leaf '{@event.Name} {@event.Cmp} {@event.Count}' cannot be evaluated in real time.", nameof(node)),
        _ => throw new NotSupportedException($"Unknown rule node type '{node.GetType()}'."),
    };

    private static bool EvaluateAttribute(AttributeNode attribute, string? value) => attribute.Cmp switch
    {
        AttributeCmp.Exists => value is not null,
        AttributeCmp.NotExists => value is null,
        AttributeCmp.Contains => value is not null && value.Contains(((RuleValue.Text)attribute.Value!).Value, StringComparison.OrdinalIgnoreCase),
        AttributeCmp.Eq => Equal(value, attribute.Value!) == true,
        AttributeCmp.Neq => Equal(value, attribute.Value!) != true, // `(JV <> @p OR JV IS NULL)`
        AttributeCmp.In => In(value, attribute.Value!) == true,
        AttributeCmp.Nin => In(value, attribute.Value!) != true, // `(JV NOT IN (…) OR JV IS NULL)`
        AttributeCmp.Gt or AttributeCmp.Gte or AttributeCmp.Lt or AttributeCmp.Lte => Order(attribute.Cmp, TryConvertFloat(value), ((RuleValue.Number)attribute.Value!).Value),
        _ => throw new NotSupportedException($"Unknown attribute comparator '{attribute.Cmp}'."),
    };

    // null = UNKNOWN (the comparand is NULL): the key is missing, or a numeric compare could not convert it.
    private static bool? Equal(string? value, RuleValue expected) => expected switch
    {
        RuleValue.Number number => TryConvertFloat(value) is { } actual ? actual == number.Value : null,
        RuleValue.Bool boolean => value is null ? null : TextEquals(value, boolean.Value ? "true" : "false"),
        RuleValue.Text text => value is null ? null : TextEquals(value, text.Value),
        _ => throw new NotSupportedException($"'{expected.GetType()}' cannot compare by equality."),
    };

    private static bool? In(string? value, RuleValue expected) => expected switch
    {
        RuleValue.NumberList numbers => TryConvertFloat(value) is { } actual ? numbers.Values.Contains(actual) : null,
        RuleValue.TextList texts => value is null ? null : texts.Values.Any(candidate => TextEquals(value, candidate)),
        _ => throw new NotSupportedException($"'{expected.GetType()}' is not a list value."),
    };

    private static bool Order(AttributeCmp cmp, double? actual, double expected) => actual is { } a && cmp switch
    {
        AttributeCmp.Gt => a > expected,
        AttributeCmp.Gte => a >= expected,
        AttributeCmp.Lt => a < expected,
        AttributeCmp.Lte => a <= expected,
        _ => throw new NotSupportedException($"'{cmp}' is not an ordering comparator."),
    };

    // SQL `=` pads the shorter operand with spaces, so trailing spaces never decide equality; the collation is
    // case-insensitive. Ordinal-ignore-case is the documented approximation of the collation's linguistic rules.
    private static bool TextEquals(string left, string right)
        => string.Equals(left.TrimEnd(' '), right.TrimEnd(' '), StringComparison.OrdinalIgnoreCase);

    /// <summary>`TRY_CONVERT(float, text)`: surrounding spaces allowed, an empty or all-space string is 0, a sign,
    /// decimal point and exponent are accepted, anything else (thousands separators, hex, `NaN`, `Infinity`) is
    /// NULL.</summary>
    internal static double? TryConvertFloat(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim(' ');
        if (trimmed.Length == 0)
        {
            return 0;
        }

        const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
        return double.TryParse(trimmed, styles, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) ? parsed : null;
    }

    /// <summary>What `JSON_VALUE(p.Attributes, '$.key')` (or the promoted computed column) returns: a string's
    /// text, a number's JSON text, `true`/`false`, or NULL for null, objects, arrays, a missing key and a string
    /// over 4,000 characters. The property lookup is case-sensitive, like a JSON path.</summary>
    internal static string? Resolve(JsonElement attributes, string key)
    {
        if (attributes.ValueKind != JsonValueKind.Object || !attributes.TryGetProperty(key, out var property))
        {
            return null;
        }

        var value = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() is { Length: <= JsonValueMaxLength } text ? text : null,
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };

        return value is { Length: > PromotedColumnLength } && PromotedAttributeColumns.TryGetColumn(key, out _)
            ? value[..PromotedColumnLength]
            : value;
    }
}
