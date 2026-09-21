using System.Text.Json;
using Crm.Segmentation.Domain.Rules;
using FluentAssertions;
using Xunit;

namespace Crm.Segmentation.UnitTests;

public class DefinitionHashTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void UT058_different_serializations_of_the_same_tree_hash_identically()
    {
        var (canonicalOrder, _) = Fixtures.Load("group-nested");
        var reordered = Json("""
            {"children":[
                {"value":"pro","cmp":"eq","key":"plan","type":"attribute"},
                {"count":3.0,"window":"30d","name":"purchase","cmp":"gte","type":"event"},
                {"children":[
                    {"value":["ID","SG"],"cmp":"in","key":"country","type":"attribute"},
                    {"count":0,"window":"7d","name":"app_open","cmp":"eq","type":"event"}
                ],"op":"or"}
            ],"op":"and"}
            """);
        var whitespaced = Json("""
            {
              "op" : "and" ,
              "children" : [
                { "type" : "attribute", "key" : "plan", "cmp" : "eq", "value" : "pro" },
                { "type" : "event", "name" : "purchase", "cmp" : "gte", "count" : 3, "window" : "30d" },
                { "op" : "or", "children" : [
                  { "type" : "attribute", "key" : "country", "cmp" : "in", "value" : [ "ID", "SG" ] },
                  { "type" : "event", "name" : "app_open", "cmp" : "eq", "count" : 0, "window" : "7d" }
                ] }
              ]
            }
            """);

        var hashA = DefinitionHash.Compute(RuleParser.Parse(canonicalOrder).Tree!);
        var hashB = DefinitionHash.Compute(RuleParser.Parse(reordered).Tree!);
        var hashC = DefinitionHash.Compute(RuleParser.Parse(whitespaced).Tree!);

        hashA.Should().HaveCount(32);
        hashA.Should().Equal(hashB);
        hashA.Should().Equal(hashC);
    }

    [Fact]
    public void UT058_changing_a_value_changes_the_hash()
    {
        var (original, _) = Fixtures.Load("group-nested");
        var changed = Json("""
            {"op":"and","children":[
                {"type":"attribute","key":"plan","cmp":"eq","value":"elite"},
                {"type":"event","name":"purchase","cmp":"gte","count":3,"window":"30d"},
                {"op":"or","children":[
                    {"type":"attribute","key":"country","cmp":"in","value":["ID","SG"]},
                    {"type":"event","name":"app_open","cmp":"eq","count":0,"window":"7d"}
                ]}
            ]}
            """);

        var originalHash = DefinitionHash.ToHex(DefinitionHash.Compute(RuleParser.Parse(original).Tree!));
        var changedHash = DefinitionHash.ToHex(DefinitionHash.Compute(RuleParser.Parse(changed).Tree!));

        originalHash.Should().NotBe(changedHash);
    }

    [Fact]
    public void ToHex_is_lower_case_and_64_characters()
    {
        var (definition, _) = Fixtures.Load("attr-eq");

        var hex = DefinitionHash.ToHex(DefinitionHash.Compute(RuleParser.Parse(definition).Tree!));

        hex.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void canonical_text_of_attr_eq_matches_the_spec_example()
    {
        var (definition, _) = Fixtures.Load("attr-eq");
        var tree = RuleParser.Parse(definition).Tree!;

        CanonicalRuleWriter.Write(tree).Should().Be("""{"type":"attribute","key":"plan","cmp":"eq","value":"pro"}""");
    }

    [Fact]
    public void canonical_form_omits_value_for_exists_and_not_exists()
    {
        var (definition, _) = Fixtures.Load("attr-exists");
        var tree = RuleParser.Parse(definition).Tree!;

        CanonicalRuleWriter.Write(tree).Should().Be("""{"type":"attribute","key":"tier","cmp":"exists"}""");
    }

    [Theory]
    [MemberData(nameof(Fixtures.Valid), MemberType = typeof(Fixtures))]
    public void round_trip_parse_of_the_canonical_form_reproduces_the_tree(string name)
    {
        var (definition, _) = Fixtures.Load(name);
        var tree = RuleParser.Parse(definition).Tree!;

        var roundTripped = RuleParser.Parse(Json(CanonicalRuleWriter.Write(tree))).Tree;

        roundTripped.Should().Be(tree);
    }
}
