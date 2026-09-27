using System.Text.Json;
using Crm.Segmentation.Application.Realtime;
using Crm.Segmentation.Domain.Rules;
using FluentAssertions;
using Xunit;

namespace Crm.Segmentation.UnitTests;

public class RealtimeEvaluatorTests
{
    private static readonly Func<EventNode, long> NoCounters = _ => 0;

    private static AttributeNode Attribute(string key, AttributeCmp cmp, RuleValue? value = null) => new(key, cmp, value);

    private static JsonElement Snapshot(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static bool Evaluate(RuleNode tree, string attributesJson) => RealtimeEvaluator.Evaluate(tree, Snapshot(attributesJson), NoCounters);

    [Fact]
    public void UT046_evaluator_applies_attribute_leaves_to_the_snapshot()
    {
        const string snapshot = """{"plan":"pro","country":"ID"}""";

        Evaluate(Attribute("plan", AttributeCmp.Eq, new RuleValue.Text("pro")), snapshot).Should().BeTrue();
        Evaluate(Attribute("country", AttributeCmp.Nin, new RuleValue.TextList(["ID"])), snapshot).Should().BeFalse();
        Evaluate(Attribute("tier", AttributeCmp.NotExists), snapshot).Should().BeTrue();
        Evaluate(Attribute("ltv", AttributeCmp.Gt, new RuleValue.Number(10)), snapshot).Should().BeFalse("a missing ltv compares UNKNOWN");
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void UT047_evaluator_compares_counters_to_event_leaves(long counter, bool expected)
    {
        var leaf = new EventNode("purchase", EventCmp.Gte, 3, new RuleWindow(30, 'd'));

        RealtimeEvaluator.Evaluate(leaf, Snapshot("{}"), node => node == leaf ? counter : 0).Should().Be(expected);
    }

    [Fact]
    public void UT047_a_missing_counter_is_zero()
    {
        var leaf = new EventNode("purchase", EventCmp.Gte, 1, new RuleWindow(30, 'd'));

        RealtimeEvaluator.Evaluate(leaf, Snapshot("{}"), NoCounters).Should().BeFalse();
    }

    [Theory]
    [InlineData(true, true, Transition.None)]
    [InlineData(true, false, Transition.Exited)]
    [InlineData(false, true, Transition.Entered)]
    [InlineData(false, false, Transition.None)]
    public void UT048_transition_detection(bool previous, bool current, Transition expected)
        => Transitions.From(previous, current).Should().Be(expected);

    // I1: text comparisons follow the database's case-insensitive collation and ignore trailing spaces like `=`.
    [Theory]
    [InlineData("""{"plan":"PRO"}""", true)]
    [InlineData("""{"plan":"pro  "}""", true)]
    [InlineData("""{"plan":"  pro"}""", false)]
    [InlineData("""{"plan":"pros"}""", false)]
    [InlineData("""{"plan":null}""", false)]
    [InlineData("""{"plan":{"tier":"pro"}}""", false)]
    [InlineData("""{"plan":["pro"]}""", false)]
    [InlineData("""{}""", false)]
    public void I1_text_eq_follows_the_sql_collation(string snapshot, bool expected)
        => Evaluate(Attribute("plan", AttributeCmp.Eq, new RuleValue.Text("pro")), snapshot).Should().Be(expected);

    [Theory]
    [InlineData("""{"plan":"free"}""", true)]
    [InlineData("""{"plan":"Pro"}""", false)]
    [InlineData("""{}""", true)]
    [InlineData("""{"plan":null}""", true)]
    public void I1_text_neq_includes_a_missing_key(string snapshot, bool expected)
        => Evaluate(Attribute("plan", AttributeCmp.Neq, new RuleValue.Text("pro")), snapshot).Should().Be(expected);

    // JSON_VALUE returns a number's JSON text and TRY_CONVERT(float, ...) parses it (or a numeric string) back.
    [Theory]
    [InlineData("""{"ltv":10}""", true)]
    [InlineData("""{"ltv":10.0}""", true)]
    [InlineData("""{"ltv":1e1}""", true)]
    [InlineData("""{"ltv":"10"}""", true)]
    [InlineData("""{"ltv":" 10 "}""", true)]
    [InlineData("""{"ltv":"ten"}""", false)]
    [InlineData("""{"ltv":true}""", false)]
    [InlineData("""{}""", false)]
    public void I1_numeric_eq_converts_like_try_convert(string snapshot, bool expected)
        => Evaluate(Attribute("ltv", AttributeCmp.Eq, new RuleValue.Number(10)), snapshot).Should().Be(expected);

    [Theory]
    [InlineData("""{"ltv":11}""", true)]
    [InlineData("""{"ltv":10}""", false)]
    [InlineData("""{"ltv":"ten"}""", true)]
    [InlineData("""{}""", true)]
    public void I1_numeric_neq_includes_an_unconvertible_value(string snapshot, bool expected)
        => Evaluate(Attribute("ltv", AttributeCmp.Neq, new RuleValue.Number(10)), snapshot).Should().Be(expected);

    [Theory]
    [InlineData(AttributeCmp.Gt, 10.5, false)]
    [InlineData(AttributeCmp.Gt, 10.4, true)]
    [InlineData(AttributeCmp.Gte, 10.5, true)]
    [InlineData(AttributeCmp.Lt, 10.6, true)]
    [InlineData(AttributeCmp.Lte, 10.5, true)]
    [InlineData(AttributeCmp.Lte, 10.4, false)]
    public void I1_ordering_compares_numerically(AttributeCmp cmp, double value, bool expected)
        => Evaluate(Attribute("ltv", cmp, new RuleValue.Number(value)), """{"ltv":"10.5"}""").Should().Be(expected);

    [Fact]
    public void I1_ordering_on_a_non_numeric_value_is_false_both_ways()
    {
        Evaluate(Attribute("ltv", AttributeCmp.Gt, new RuleValue.Number(0)), """{"ltv":"high"}""").Should().BeFalse();
        Evaluate(Attribute("ltv", AttributeCmp.Lte, new RuleValue.Number(0)), """{"ltv":"high"}""").Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"country":"sg"}""", true)]
    [InlineData("""{"country":"US"}""", false)]
    [InlineData("""{}""", false)]
    public void I1_text_in(string snapshot, bool expected)
        => Evaluate(Attribute("country", AttributeCmp.In, new RuleValue.TextList(["ID", "SG"])), snapshot).Should().Be(expected);

    [Theory]
    [InlineData("""{"country":"sg"}""", false)]
    [InlineData("""{"country":"US"}""", true)]
    [InlineData("""{}""", true)]
    public void I1_text_nin_includes_a_missing_key(string snapshot, bool expected)
        => Evaluate(Attribute("country", AttributeCmp.Nin, new RuleValue.TextList(["ID", "SG"])), snapshot).Should().Be(expected);

    [Theory]
    [InlineData("""{"level":2}""", true, false)]
    [InlineData("""{"level":"3.0"}""", true, false)]
    [InlineData("""{"level":4}""", false, true)]
    [InlineData("""{"level":"x"}""", false, true)]
    public void I1_numeric_in_and_nin(string snapshot, bool inExpected, bool ninExpected)
    {
        var values = new RuleValue.NumberList([2, 3]);

        Evaluate(Attribute("level", AttributeCmp.In, values), snapshot).Should().Be(inExpected);
        Evaluate(Attribute("level", AttributeCmp.Nin, values), snapshot).Should().Be(ninExpected);
    }

    // The compiler escapes LIKE metacharacters, so `contains` is a literal, case-insensitive substring test.
    [Theory]
    [InlineData("""{"campaign":"Summer 50% OFF"}""", "50% off", true)]
    [InlineData("""{"campaign":"Summer 500 OFF"}""", "50% off", false)]
    [InlineData("""{"campaign":"a_b"}""", "a_b", true)]
    [InlineData("""{"campaign":"axb"}""", "a_b", false)]
    [InlineData("""{"campaign":"[x]"}""", "[x]", true)]
    [InlineData("""{"campaign":"anything"}""", "", true)]
    [InlineData("""{}""", "", false)]
    public void I1_contains_is_a_literal_case_insensitive_substring(string snapshot, string needle, bool expected)
        => Evaluate(Attribute("campaign", AttributeCmp.Contains, new RuleValue.Text(needle)), snapshot).Should().Be(expected);

    [Theory]
    [InlineData("""{"vip":true}""", true)]
    [InlineData("""{"vip":"TRUE"}""", true)]
    [InlineData("""{"vip":false}""", false)]
    [InlineData("""{"vip":1}""", false)]
    public void I1_booleans_compare_as_the_strings_true_and_false(string snapshot, bool expected)
        => Evaluate(Attribute("vip", AttributeCmp.Eq, new RuleValue.Bool(true)), snapshot).Should().Be(expected);

    [Theory]
    [InlineData("""{"tier":"gold"}""", true)]
    [InlineData("""{"tier":""}""", true)]
    [InlineData("""{"tier":0}""", true)]
    [InlineData("""{"tier":null}""", false)]
    [InlineData("""{"tier":{}}""", false)]
    [InlineData("""{}""", false)]
    public void I1_exists_follows_json_value(string snapshot, bool expected)
    {
        Evaluate(Attribute("tier", AttributeCmp.Exists), snapshot).Should().Be(expected);
        Evaluate(Attribute("tier", AttributeCmp.NotExists), snapshot).Should().Be(!expected);
    }

    [Fact]
    public void I1_attribute_keys_are_case_sensitive_like_a_json_path()
        => Evaluate(Attribute("plan", AttributeCmp.Exists), """{"Plan":"pro"}""").Should().BeFalse();

    [Fact]
    public void I1_a_promoted_key_compares_its_first_256_characters_like_the_computed_column()
    {
        var head = new string('a', 256);
        var snapshot = JsonSerializer.Serialize(new Dictionary<string, string> { ["plan"] = head + "tail", ["other"] = head + "tail" });

        Evaluate(Attribute("plan", AttributeCmp.Eq, new RuleValue.Text(head)), snapshot).Should().BeTrue("Attr_plan is CAST(... AS nvarchar(256))");
        Evaluate(Attribute("other", AttributeCmp.Eq, new RuleValue.Text(head)), snapshot).Should().BeFalse("a JSON_VALUE key is not truncated");
    }

    [Fact]
    public void I1_a_string_longer_than_4000_characters_is_absent_like_lax_json_value()
    {
        var snapshot = JsonSerializer.Serialize(new Dictionary<string, string> { ["note"] = new('n', 4001) });

        Evaluate(Attribute("note", AttributeCmp.Exists), snapshot).Should().BeFalse();
    }

    [Fact]
    public void Non_object_attributes_are_all_absent()
    {
        RealtimeEvaluator.Evaluate(Attribute("plan", AttributeCmp.NotExists), default, NoCounters).Should().BeTrue();
        Evaluate(Attribute("plan", AttributeCmp.NotExists), "[]").Should().BeTrue();
    }

    [Fact]
    public void Groups_combine_leaves_and_counters()
    {
        var purchase = new EventNode("purchase", EventCmp.Gte, 2, new RuleWindow(30, 'd'));
        var appOpen = new EventNode("app_open", EventCmp.Gte, 1, new RuleWindow(7, 'd'));
        var tree = new GroupNode(GroupOp.And,
        [
            purchase,
            new GroupNode(GroupOp.Or, [appOpen, Attribute("plan", AttributeCmp.Eq, new RuleValue.Text("pro"))]),
        ]);
        var counters = new Dictionary<EventNode, long> { [purchase] = 2 };

        RealtimeEvaluator.Evaluate(tree, Snapshot("""{"plan":"free"}"""), node => counters.GetValueOrDefault(node)).Should().BeFalse();

        counters[appOpen] = 1;
        RealtimeEvaluator.Evaluate(tree, Snapshot("""{"plan":"free"}"""), node => counters.GetValueOrDefault(node)).Should().BeTrue();
    }

    [Fact]
    public void An_ineligible_tree_is_refused_rather_than_misjudged()
    {
        var not = new GroupNode(GroupOp.Not, [Attribute("plan", AttributeCmp.Exists)]);
        var zero = new EventNode("app_open", EventCmp.Eq, 0, new RuleWindow(7, 'd'));

        FluentActions.Invoking(() => Evaluate(not, "{}")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => Evaluate(zero, "{}")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Seed_purchasers_fixture_is_evaluated_from_its_counter()
    {
        var parsed = RuleParser.Parse(Fixtures.Load("seed-purchasers").Definition);

        RealtimeEvaluator.Evaluate(parsed.Tree!, Snapshot("{}"), _ => 1).Should().BeTrue();
        RealtimeEvaluator.Evaluate(parsed.Tree!, Snapshot("{}"), _ => 0).Should().BeFalse();
    }
}
