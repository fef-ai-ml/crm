using System.Text.Json;
using Crm.Segmentation.Domain.Rules;
using FluentAssertions;
using Xunit;

namespace Crm.Segmentation.UnitTests;

public class RuleValidatorTests
{
    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static JsonElement NestedAndGroups(int depth)
    {
        var json = """{"type":"attribute","key":"plan","cmp":"eq","value":"pro"}""";
        for (var i = 0; i < depth; i++)
        {
            json = $$"""{"op":"and","children":[{{json}}]}""";
        }

        return Json(json);
    }

    private static JsonElement FlatAndLeaves(int count)
    {
        var leaves = Enumerable.Range(1, count).Select(i => $$"""{"type":"attribute","key":"k{{i:D2}}","cmp":"exists"}""");
        return Json($$"""{"op":"and","children":[{{string.Join(",", leaves)}}]}""");
    }

    private static JsonElement NotWithChildren(int count)
    {
        var leaves = Enumerable.Range(1, count).Select(i => $$"""{"type":"attribute","key":"k{{i:D2}}","cmp":"exists"}""");
        return Json($$"""{"op":"not","children":[{{string.Join(",", leaves)}}]}""");
    }

    private static JsonElement EventWithWindow(string window) =>
        Json($$"""{"type":"event","name":"purchase","cmp":"gte","count":1,"window":"{{window}}"}""");

    private static JsonElement AttributeWithKey(string key) =>
        Json($$"""{"type":"attribute","key":"{{key}}","cmp":"eq","value":"pro"}""");

    [Fact]
    public void UT016_depth_5_passes_and_depth_6_fails()
    {
        RuleParser.Parse(NestedAndGroups(5)).IsValid.Should().BeTrue();

        var result = RuleParser.Parse(NestedAndGroups(6));

        result.IsValid.Should().BeFalse();
        result.Errors[0].Should().Be(new RuleError("definition", "depth must be at most 5"));
    }

    [Fact]
    public void UT017_20_leaves_pass_and_21_fail()
    {
        RuleParser.Parse(FlatAndLeaves(20)).IsValid.Should().BeTrue();

        var result = RuleParser.Parse(FlatAndLeaves(21));

        result.IsValid.Should().BeFalse();
        result.Errors[0].Should().Be(new RuleError("definition", "at most 20 leaves"));
    }

    [Fact]
    public void UT018_not_with_one_child_passes() =>
        RuleParser.Parse(NotWithChildren(1)).IsValid.Should().BeTrue();

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void UT018_not_requires_exactly_one_child(int childCount)
    {
        var result = RuleParser.Parse(NotWithChildren(childCount));

        result.IsValid.Should().BeFalse();
        result.Errors[0].Should().Be(new RuleError("definition", "not must have exactly one child"));
    }

    [Fact]
    public void UT018_nested_not_arity_error_reports_the_groups_own_path()
    {
        var definition = Json("""
            {"op":"and","children":[
                {"type":"attribute","key":"plan","cmp":"eq","value":"pro"},
                {"op":"not","children":[
                    {"type":"attribute","key":"tier","cmp":"eq","value":"gold"},
                    {"type":"attribute","key":"country","cmp":"eq","value":"ID"}
                ]}
            ]}
            """);

        var result = RuleParser.Parse(definition);

        result.Errors.Should().ContainSingle()
            .Which.Should().Be(new RuleError("definition.children[1]", "not must have exactly one child"));
    }

    [Fact]
    public void and_or_groups_require_at_least_one_child()
    {
        var result = RuleParser.Parse(Json("""{"op":"and","children":[]}"""));

        result.IsValid.Should().BeFalse();
        result.Errors[0].Should().Be(new RuleError("definition", "group must have at least one child"));
    }

    [Theory]
    [InlineData("30d")]
    [InlineData("12h")]
    [InlineData("365d")]
    [InlineData("8760h")]
    public void UT019_window_within_range_passes(string window) =>
        RuleParser.Parse(EventWithWindow(window)).IsValid.Should().BeTrue();

    [Theory]
    [InlineData("366d")]
    [InlineData("8761h")]
    [InlineData("30")]
    [InlineData("1w")]
    [InlineData("0d")]
    [InlineData("0h")]
    public void UT019_window_out_of_range_or_malformed_fails(string window)
    {
        var result = RuleParser.Parse(EventWithWindow(window));

        result.IsValid.Should().BeFalse();
        result.Errors[0].Should().Be(new RuleError("definition.window", "window must match ^\\d+(d|h)$ and be at most 365 days"));
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("a_b1")]
    public void UT020_key_pattern_passes(string key) =>
        RuleParser.Parse(AttributeWithKey(key)).IsValid.Should().BeTrue();

    [Fact]
    public void UT020_64_character_key_passes() =>
        RuleParser.Parse(AttributeWithKey(new string('x', 64))).IsValid.Should().BeTrue();

    [Theory]
    [InlineData("plan.name")]
    [InlineData("plan'")]
    public void UT020_key_pattern_fails(string key)
    {
        var result = RuleParser.Parse(AttributeWithKey(key));

        result.IsValid.Should().BeFalse();
        result.Errors[0].Should().Be(new RuleError("definition.key", "key must match ^[A-Za-z0-9_]{1,64}$"));
    }

    [Fact]
    public void UT020_65_character_key_fails()
    {
        var result = RuleParser.Parse(AttributeWithKey(new string('x', 65)));

        result.IsValid.Should().BeFalse();
        result.Errors[0].Should().Be(new RuleError("definition.key", "key must match ^[A-Za-z0-9_]{1,64}$"));
    }

    // H17 value typing, one case per rule.

    [Fact]
    public void H17_numeric_comparators_require_a_number()
    {
        var result = RuleParser.Parse(Json("""{"type":"attribute","key":"ltv","cmp":"gt","value":"100"}"""));

        result.Errors[0].Should().Be(new RuleError("definition.value", "value must be a number"));
    }

    [Fact]
    public void H17_numeric_comparators_require_a_value()
    {
        var result = RuleParser.Parse(Json("""{"type":"attribute","key":"ltv","cmp":"gt"}"""));

        result.Errors[0].Should().Be(new RuleError("definition.value", "value is required"));
    }

    [Fact]
    public void H17_contains_requires_a_string()
    {
        var result = RuleParser.Parse(Json("""{"type":"attribute","key":"campaign","cmp":"contains","value":50}"""));

        result.Errors[0].Should().Be(new RuleError("definition.value", "value must be a string"));
    }

    [Fact]
    public void H17_strings_are_limited_to_256_characters()
    {
        var value = new string('a', 257);
        var result = RuleParser.Parse(Json($$"""{"type":"attribute","key":"campaign","cmp":"contains","value":"{{value}}"}"""));

        result.Errors[0].Should().Be(new RuleError("definition.value", "value must be at most 256 characters"));
    }

    [Fact]
    public void H17_in_requires_a_non_empty_array()
    {
        var result = RuleParser.Parse(Json("""{"type":"attribute","key":"country","cmp":"in","value":[]}"""));

        result.Errors[0].Should().Be(new RuleError("definition.value", "value must be a non-empty array of strings or of numbers (at most 100)"));
    }

    [Fact]
    public void H17_in_rejects_mixed_element_types()
    {
        var result = RuleParser.Parse(Json("""{"type":"attribute","key":"country","cmp":"in","value":["ID",1]}"""));

        result.Errors[0].Should().Be(new RuleError("definition.value", "value must be a non-empty array of strings or of numbers (at most 100)"));
    }

    [Fact]
    public void H17_in_rejects_more_than_100_elements()
    {
        var values = string.Join(",", Enumerable.Range(0, 101).Select(i => $"\"v{i}\""));
        var result = RuleParser.Parse(Json($$"""{"type":"attribute","key":"country","cmp":"in","value":[{{values}}]}"""));

        result.Errors[0].Should().Be(new RuleError("definition.value", "value must be a non-empty array of strings or of numbers (at most 100)"));
    }

    [Fact]
    public void H17_exists_forbids_a_value()
    {
        var result = RuleParser.Parse(Json("""{"type":"attribute","key":"tier","cmp":"exists","value":"gold"}"""));

        result.Errors[0].Should().Be(new RuleError("definition.value", "value is not allowed for this comparator"));
    }

    [Fact]
    public void H17_eq_requires_a_value()
    {
        var result = RuleParser.Parse(Json("""{"type":"attribute","key":"plan","cmp":"eq"}"""));

        result.Errors[0].Should().Be(new RuleError("definition.value", "value is required"));
    }

    // M-2: eq/neq accept string, number, or bool — the message for anything else should say so, not overclaim
    // "a string" specifically.

    [Theory]
    [InlineData("""["a","b"]""")]
    [InlineData("""{"nested":true}""")]
    [InlineData("null")]
    public void M2_eq_with_a_non_scalar_value_reports_the_accepted_types(string valueLiteral)
    {
        var result = RuleParser.Parse(Json($$"""{"type":"attribute","key":"plan","cmp":"eq","value":{{valueLiteral}}}"""));

        result.Errors[0].Should().Be(new RuleError("definition.value", "value must be a string, number or boolean"));
    }

    [Fact]
    public void unknown_comparator_is_rejected()
    {
        var result = RuleParser.Parse(Json("""{"type":"attribute","key":"plan","cmp":"bogus","value":"pro"}"""));

        result.Errors[0].Should().Be(new RuleError("definition.cmp", "unknown comparator"));
    }

    [Fact]
    public void unknown_node_type_is_rejected()
    {
        var result = RuleParser.Parse(Json("""{"type":"bogus"}"""));

        result.Errors[0].Should().Be(new RuleError("definition.type", "unknown node type"));
    }

    [Fact]
    public void unknown_group_operator_is_rejected()
    {
        var result = RuleParser.Parse(Json("""{"op":"xor","children":[{"type":"attribute","key":"plan","cmp":"eq","value":"pro"}]}"""));

        result.Errors[0].Should().Be(new RuleError("definition.op", "unknown group operator"));
    }

    [Fact]
    public void event_count_must_be_a_non_negative_integer()
    {
        RuleParser.Parse(Json("""{"type":"event","name":"purchase","cmp":"gte","count":-1,"window":"30d"}"""))
            .Errors[0].Should().Be(new RuleError("definition.count", "count must be at least 0"));

        RuleParser.Parse(Json("""{"type":"event","name":"purchase","cmp":"gte","count":1.5,"window":"30d"}"""))
            .Errors[0].Should().Be(new RuleError("definition.count", "count must be at least 0"));
    }

    // I-2: two H23 edges the review found untested, plus the extreme-exponent case it asked to harden.

    [Fact]
    public void I2_a_count_greater_than_int_max_value_is_rejected()
    {
        var result = RuleParser.Parse(Json("""{"type":"event","name":"purchase","cmp":"gte","count":2147483648,"window":"30d"}"""));

        result.Errors[0].Should().Be(new RuleError("definition.count", "count must be at least 0"));
    }

    [Fact]
    public void I2_a_string_count_is_rejected()
    {
        var result = RuleParser.Parse(Json("""{"type":"event","name":"purchase","cmp":"gte","count":"3","window":"30d"}"""));

        result.Errors[0].Should().Be(new RuleError("definition.count", "count must be at least 0"));
    }

    [Theory]
    [InlineData("3.0")]
    [InlineData("3e0")]
    public void I2_whole_number_counts_in_any_numeric_form_are_accepted(string countLiteral)
    {
        var result = RuleParser.Parse(Json($$"""{"type":"event","name":"purchase","cmp":"gte","count":{{countLiteral}},"window":"30d"}"""));

        result.IsValid.Should().BeTrue();
        ((EventNode)result.Tree!).Count.Should().Be(3);
    }

    [Fact]
    public void I2_an_extreme_exponent_count_is_rejected_without_throwing()
    {
        var result = RuleParser.Parse(Json("""{"type":"event","name":"purchase","cmp":"gte","count":1e400,"window":"30d"}"""));

        result.Errors[0].Should().Be(new RuleError("definition.count", "count must be at least 0"));
    }

    // C-1: a window amount that overflows Int32 must be rejected, not thrown.

    [Theory]
    [InlineData("99999999999d")]
    [InlineData("99999999999h")]
    [InlineData("2147483648d")]
    public void C1_window_amounts_that_overflow_an_int_are_rejected_without_throwing(string window)
    {
        var result = RuleParser.Parse(EventWithWindow(window));

        result.Errors[0].Should().Be(new RuleError("definition.window", "window must match ^\\d+(d|h)$ and be at most 365 days"));
    }

    // I-1: \d matches Unicode decimal digits generally, not just ASCII; the pattern and int.Parse must not
    // silently accept (or throw on) a window built from them.

    [Theory]
    [InlineData("３０d")] // full-width "30"
    [InlineData("٣٠d")] // Arabic-Indic "30"
    public void I1_non_ascii_digit_windows_are_rejected_without_throwing(string window)
    {
        var result = RuleParser.Parse(EventWithWindow(window));

        result.Errors[0].Should().Be(new RuleError("definition.window", "window must match ^\\d+(d|h)$ and be at most 365 days"));
    }

    [Fact]
    public void event_name_is_required_and_bounded()
    {
        RuleParser.Parse(Json("""{"type":"event","name":"","cmp":"gte","count":1,"window":"30d"}"""))
            .Errors[0].Should().Be(new RuleError("definition.name", "name is required"));

        var longName = new string('e', 129);
        RuleParser.Parse(Json($$"""{"type":"event","name":"{{longName}}","cmp":"gte","count":1,"window":"30d"}"""))
            .Errors[0].Should().Be(new RuleError("definition.name", "name is required"));
    }

    [Fact]
    public void event_cmp_outside_eq_gte_lte_is_unknown_comparator()
    {
        var result = RuleParser.Parse(Json("""{"type":"event","name":"purchase","cmp":"gt","count":1,"window":"30d"}"""));

        result.Errors[0].Should().Be(new RuleError("definition.cmp", "unknown comparator"));
    }

    // The compiler (a later task) must never see a rejected key.

    [Fact]
    public void the_parsed_tree_is_null_when_the_definition_is_invalid()
    {
        var result = RuleParser.Parse(AttributeWithKey("plan'"));

        result.Tree.Should().BeNull();
    }
}
