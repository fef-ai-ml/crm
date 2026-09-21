using System.Globalization;
using System.Text.Json;

namespace Crm.Segmentation.Domain.Rules;

/// <summary>Parses and validates a rule definition (spec §5.1). Pure; no I/O; <see cref="System.Text.Json"/> only.
/// Errors are collected in pre-order, then depth, then leaf count (H19); building continues past a field
/// error with a best-effort default so later siblings are still checked.</summary>
public static class RuleParser
{
    public static RuleParseResult Parse(JsonElement definition)
    {
        var errors = new List<RuleError>();
        var tree = ParseNode(definition, "definition", errors);

        if (tree is not null)
        {
            var depth = DepthOf(tree);
            if (depth > RuleLimits.MaxDepth)
            {
                errors.Add(new RuleError("definition", $"depth must be at most {RuleLimits.MaxDepth}"));
            }

            var leafCount = CountLeaves(tree);
            if (leafCount > RuleLimits.MaxLeaves)
            {
                errors.Add(new RuleError("definition", $"at most {RuleLimits.MaxLeaves} leaves"));
            }
        }

        return new RuleParseResult(errors.Count == 0 ? tree : null, errors);
    }

    private static RuleNode? ParseNode(JsonElement node, string path, List<RuleError> errors)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new RuleError(path, "unknown node type"));
            return null;
        }

        if (node.TryGetProperty("op", out var opProperty))
        {
            return ParseGroup(node, opProperty, path, errors);
        }

        return GetString(node, "type") switch
        {
            "attribute" => ParseAttribute(node, path, errors),
            "event" => ParseEvent(node, path, errors),
            _ => FailNode(node, path, ".type", "unknown node type", errors),
        };
    }

    private static AttributeNode FailNode(JsonElement node, string path, string fieldSuffix, string message, List<RuleError> errors)
    {
        errors.Add(new RuleError(path + fieldSuffix, message));
        return new AttributeNode(GetString(node, "key") ?? string.Empty, AttributeCmp.Eq, null);
    }

    private static GroupNode ParseGroup(JsonElement node, JsonElement opProperty, string path, List<RuleError> errors)
    {
        var op = opProperty.ValueKind == JsonValueKind.String
            ? opProperty.GetString() switch
            {
                "and" => GroupOp.And,
                "or" => GroupOp.Or,
                "not" => GroupOp.Not,
                _ => (GroupOp?)null,
            }
            : null;

        if (op is null)
        {
            errors.Add(new RuleError($"{path}.op", "unknown group operator"));
            op = GroupOp.And;
        }

        var hasChildrenArray = node.TryGetProperty("children", out var childrenProperty) && childrenProperty.ValueKind == JsonValueKind.Array;
        var rawCount = hasChildrenArray ? childrenProperty.GetArrayLength() : 0;

        // Node-level errors before children's, so pre-order puts a group's own arity error first (H19).
        if (op == GroupOp.Not && rawCount != 1)
        {
            errors.Add(new RuleError(path, "not must have exactly one child"));
        }
        else if (op != GroupOp.Not && rawCount < 1)
        {
            errors.Add(new RuleError(path, "group must have at least one child"));
        }

        var children = new List<RuleNode>();
        if (hasChildrenArray)
        {
            var index = 0;
            foreach (var childElement in childrenProperty.EnumerateArray())
            {
                var child = ParseNode(childElement, $"{path}.children[{index}]", errors);
                if (child is not null)
                {
                    children.Add(child);
                }

                index++;
            }
        }

        return new GroupNode(op.Value, children);
    }

    private static AttributeNode ParseAttribute(JsonElement node, string path, List<RuleError> errors)
    {
        var key = GetString(node, "key") ?? string.Empty;
        if (!RuleLimits.KeyPattern().IsMatch(key))
        {
            errors.Add(new RuleError($"{path}.key", "key must match ^[A-Za-z0-9_]{1,64}$"));
        }

        var cmpText = GetString(node, "cmp") ?? string.Empty;
        var cmp = cmpText switch
        {
            "eq" => AttributeCmp.Eq,
            "neq" => AttributeCmp.Neq,
            "in" => AttributeCmp.In,
            "nin" => AttributeCmp.Nin,
            "gt" => AttributeCmp.Gt,
            "gte" => AttributeCmp.Gte,
            "lt" => AttributeCmp.Lt,
            "lte" => AttributeCmp.Lte,
            "contains" => AttributeCmp.Contains,
            "exists" => AttributeCmp.Exists,
            "not_exists" => AttributeCmp.NotExists,
            _ => (AttributeCmp?)null,
        };

        if (cmp is null)
        {
            errors.Add(new RuleError($"{path}.cmp", "unknown comparator"));
        }

        var value = ParseAttributeValue(node, cmp, $"{path}.value", errors);

        return new AttributeNode(key, cmp ?? AttributeCmp.Eq, value);
    }

    private static RuleValue? ParseAttributeValue(JsonElement node, AttributeCmp? cmp, string valuePath, List<RuleError> errors)
    {
        var hasValue = node.TryGetProperty("value", out var valueElement) && valueElement.ValueKind != JsonValueKind.Undefined;

        switch (cmp)
        {
            case AttributeCmp.Exists or AttributeCmp.NotExists:
                if (hasValue)
                {
                    errors.Add(new RuleError(valuePath, "value is not allowed for this comparator"));
                }

                return null;

            case AttributeCmp.Gt or AttributeCmp.Gte or AttributeCmp.Lt or AttributeCmp.Lte:
                if (!hasValue)
                {
                    errors.Add(new RuleError(valuePath, "value is required"));
                    return null;
                }

                if (valueElement.ValueKind != JsonValueKind.Number)
                {
                    errors.Add(new RuleError(valuePath, "value must be a number"));
                    return null;
                }

                return new RuleValue.Number(valueElement.GetDouble());

            case AttributeCmp.Contains:
                if (!hasValue)
                {
                    errors.Add(new RuleError(valuePath, "value is required"));
                    return null;
                }

                if (valueElement.ValueKind != JsonValueKind.String)
                {
                    errors.Add(new RuleError(valuePath, "value must be a string"));
                    return null;
                }

                return ParseBoundedString(valueElement.GetString()!, valuePath, errors);

            case AttributeCmp.In or AttributeCmp.Nin:
                if (!hasValue)
                {
                    errors.Add(new RuleError(valuePath, "value is required"));
                    return null;
                }

                return ParseListValue(valueElement, valuePath, errors);

            case AttributeCmp.Eq or AttributeCmp.Neq:
                if (!hasValue)
                {
                    errors.Add(new RuleError(valuePath, "value is required"));
                    return null;
                }

                return ParseScalarValue(valueElement, valuePath, errors);

            default: // cmp is unknown; best-effort so a later sibling's own errors are unaffected.
                return hasValue ? ParseScalarValue(valueElement, valuePath, errors) : null;
        }
    }

    private static RuleValue? ParseScalarValue(JsonElement valueElement, string path, List<RuleError> errors) =>
        valueElement.ValueKind switch
        {
            JsonValueKind.String => ParseBoundedString(valueElement.GetString()!, path, errors),
            JsonValueKind.Number => new RuleValue.Number(valueElement.GetDouble()),
            JsonValueKind.True or JsonValueKind.False => new RuleValue.Bool(valueElement.GetBoolean()),
            _ => FailValue(path, "value must be a string, number or boolean", errors),
        };

    private static RuleValue? FailValue(string path, string message, List<RuleError> errors)
    {
        errors.Add(new RuleError(path, message));
        return null;
    }

    private static RuleValue.Text? ParseBoundedString(string text, string path, List<RuleError> errors)
    {
        if (text.Length > RuleLimits.MaxStringLength)
        {
            errors.Add(new RuleError(path, "value must be at most 256 characters"));
            return null;
        }

        return new RuleValue.Text(text);
    }

    private static RuleValue? ParseListValue(JsonElement valueElement, string path, List<RuleError> errors)
    {
        const string message = "value must be a non-empty array of strings or of numbers (at most 100)";

        if (valueElement.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new RuleError(path, message));
            return null;
        }

        var items = valueElement.EnumerateArray().ToList();
        if (items.Count is 0 || items.Count > RuleLimits.MaxListLength)
        {
            errors.Add(new RuleError(path, message));
            return null;
        }

        if (items.All(item => item.ValueKind == JsonValueKind.String))
        {
            return new RuleValue.TextList(items.Select(item => item.GetString()!).ToList());
        }

        if (items.All(item => item.ValueKind == JsonValueKind.Number))
        {
            return new RuleValue.NumberList(items.Select(item => item.GetDouble()).ToList());
        }

        errors.Add(new RuleError(path, message));
        return null;
    }

    private static EventNode ParseEvent(JsonElement node, string path, List<RuleError> errors)
    {
        var name = GetString(node, "name") ?? string.Empty;
        if (name.Length is 0 || name.Length > RuleLimits.MaxEventNameLength)
        {
            errors.Add(new RuleError($"{path}.name", "name is required"));
        }

        var cmpText = GetString(node, "cmp") ?? string.Empty;
        var cmp = cmpText switch
        {
            "eq" => EventCmp.Eq,
            "gte" => EventCmp.Gte,
            "lte" => EventCmp.Lte,
            _ => (EventCmp?)null,
        };

        if (cmp is null)
        {
            errors.Add(new RuleError($"{path}.cmp", "unknown comparator"));
        }

        var count = ParseCount(node, path, errors);
        var window = ParseWindow(node, path, errors);

        return new EventNode(name, cmp ?? EventCmp.Eq, count, window);
    }

    private static int ParseCount(JsonElement node, string path, List<RuleError> errors)
    {
        // A whole-number check, not TryGetInt32: `3.0`/`3e0` are valid counts (UT-058 hashes `3.0` like `3`).
        // TryGetDouble, not GetDouble, and an explicit finiteness check: a JSON number with an extreme exponent
        // (e.g. `1e400`) is syntactically valid but has no finite double value, and must be rejected, not thrown.
        if (node.TryGetProperty("count", out var countElement)
            && countElement.ValueKind == JsonValueKind.Number
            && countElement.TryGetDouble(out var value)
            && double.IsFinite(value)
            && value >= 0
            && value <= int.MaxValue
            && value == Math.Floor(value))
        {
            return (int)value;
        }

        errors.Add(new RuleError($"{path}.count", "count must be at least 0"));
        return 0;
    }

    private static RuleWindow ParseWindow(JsonElement node, string path, List<RuleError> errors)
    {
        const string message = "window must match ^\\d+(d|h)$ and be at most 365 days";

        var text = GetString(node, "window") ?? string.Empty;
        var match = RuleLimits.WindowPattern().Match(text);

        // TryParse, not Parse: even with the regex's digit bound, never let a malformed amount throw (C-1).
        if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
        {
            errors.Add(new RuleError($"{path}.window", message));
            return default;
        }

        var unit = match.Groups[2].Value[0];
        var max = unit == 'd' ? RuleLimits.MaxWindowDays : RuleLimits.MaxWindowHours;
        if (amount < 1 || amount > max)
        {
            errors.Add(new RuleError($"{path}.window", message));
            return default;
        }

        return new RuleWindow(amount, unit);
    }

    private static string? GetString(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int DepthOf(RuleNode node) => node switch
    {
        GroupNode group => group.Children.Count == 0 ? 1 : 1 + group.Children.Max(DepthOf),
        _ => 0,
    };

    private static int CountLeaves(RuleNode node) => node switch
    {
        GroupNode group => group.Children.Sum(CountLeaves),
        _ => 1,
    };
}
