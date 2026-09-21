using System.Text.Json;
using Crm.Segmentation.Application.Compilation;
using Crm.Segmentation.Domain.Rules;
using FluentAssertions;
using Xunit;

namespace Crm.Segmentation.UnitTests;

/// <summary>Cases that are not one golden fixture's SQL text (UT-035…037, 039); see
/// <see cref="RuleCompilerGoldenTests"/> for the per-fixture theory.</summary>
[Trait("Category", "Unit")]
public class RuleCompilerTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime Clock = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);

    private static RuleCompiler NewCompiler() => new(new FakeClock(Clock));

    // Parses through RuleParser (rather than hand-building a RuleNode) so the whole path — including the
    // parser's own value-kind selection (H17) — is covered, not just the compiler in isolation.
    private static RuleNode Parse(string json) => RuleParser.Parse(JsonDocument.Parse(json).RootElement).Tree!;

    [Fact]
    public void UT035_root_always_filters_tenant_and_merged_profiles()
    {
        var leaf = new AttributeNode("plan", AttributeCmp.Eq, new RuleValue.Text("pro"));

        var compiled = NewCompiler().Compile(leaf, TenantId);

        compiled.Sql.Should().StartWith("SELECT p.Id FROM profiles.Profiles p WHERE p.TenantId = @tenant AND p.MergedIntoId IS NULL AND (");
        compiled.Parameters.Should().Contain(p => p.Name == "@tenant" && p.Kind == SqlValueKind.Guid && (Guid)p.Value == TenantId);
    }

    [Fact]
    public void UT036_promoted_keys_use_computed_columns()
    {
        var tree = new GroupNode(GroupOp.And,
        [
            new AttributeNode("plan", AttributeCmp.Exists, null),
            new AttributeNode("country", AttributeCmp.Exists, null),
            new AttributeNode("city", AttributeCmp.Exists, null),
            new AttributeNode("tier", AttributeCmp.Exists, null),
        ]);

        var compiled = NewCompiler().Compile(tree, TenantId);

        compiled.Sql.Should().Contain("p.Attr_plan IS NOT NULL");
        compiled.Sql.Should().Contain("p.Attr_country IS NOT NULL");
        compiled.Sql.Should().Contain("p.Attr_city IS NOT NULL");
        compiled.Sql.Should().Contain("JSON_VALUE(p.Attributes, '$.tier') IS NOT NULL");
    }

    [Fact]
    public void UT037_no_user_value_appears_in_sql_text()
    {
        var tree = new GroupNode(GroupOp.And,
        [
            new AttributeNode("plan", AttributeCmp.Eq, new RuleValue.Text("'; DROP TABLE x; --")),
            new AttributeNode("campaign", AttributeCmp.Contains, new RuleValue.Text("%_\\")),
        ]);

        var compiled = NewCompiler().Compile(tree, TenantId);

        compiled.Sql.Should().NotContain("DROP TABLE");
        compiled.Sql.Should().NotContain("%_\\");
        compiled.Parameters.Should().Contain(p => p.Kind == SqlValueKind.Text && (string)p.Value == "'; DROP TABLE x; --");
    }

    [Fact]
    public void UT037_an_attribute_key_that_fails_the_pattern_throws_instead_of_reaching_sql_text()
    {
        // RuleParser already enforces ^[A-Za-z0-9_]{1,64}$ on keys; this bypasses it to exercise the
        // compiler's own defense-in-depth check (task-3-brief.md).
        var leaf = new AttributeNode("$.evil", AttributeCmp.Exists, null);

        var act = () => NewCompiler().Compile(leaf, TenantId);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UT037_numeric_eq_and_in_use_try_convert()
    {
        var eq = new AttributeNode("ltv", AttributeCmp.Eq, new RuleValue.Number(42));
        var inList = new AttributeNode("ltv", AttributeCmp.In, new RuleValue.NumberList([1, 2]));

        var compiledEq = NewCompiler().Compile(eq, TenantId);
        var compiledIn = NewCompiler().Compile(inList, TenantId);

        compiledEq.Sql.Should().Contain("TRY_CONVERT(float, JSON_VALUE(p.Attributes, '$.ltv')) = @p0");
        compiledIn.Sql.Should().Contain("TRY_CONVERT(float, JSON_VALUE(p.Attributes, '$.ltv')) IN (@p0, @p1)");
    }

    [Fact]
    public void I2_numeric_neq_uses_try_convert_with_null_handling()
    {
        var tree = Parse("""{ "type": "attribute", "key": "ltv", "cmp": "neq", "value": 42 }""");

        var compiled = NewCompiler().Compile(tree, TenantId);
        var jv = "TRY_CONVERT(float, JSON_VALUE(p.Attributes, '$.ltv'))";

        compiled.Sql.Should().Contain($"({jv} <> @p0 OR {jv} IS NULL)");
        compiled.Parameters.Should().ContainSingle(p => p.Name == "@p0" && p.Kind == SqlValueKind.Float && (double)p.Value == 42);
    }

    [Fact]
    public void I2_numeric_nin_uses_try_convert_with_null_handling()
    {
        var tree = Parse("""{ "type": "attribute", "key": "ltv", "cmp": "nin", "value": [1, 2] }""");

        var compiled = NewCompiler().Compile(tree, TenantId);
        var jv = "TRY_CONVERT(float, JSON_VALUE(p.Attributes, '$.ltv'))";

        compiled.Sql.Should().Contain($"({jv} NOT IN (@p0, @p1) OR {jv} IS NULL)");
        compiled.Parameters.Should().Contain(p => p.Name == "@p0" && p.Kind == SqlValueKind.Float && (double)p.Value == 1);
        compiled.Parameters.Should().Contain(p => p.Name == "@p1" && p.Kind == SqlValueKind.Float && (double)p.Value == 2);
    }

    [Fact]
    public void I3_contains_escapes_brackets()
    {
        var tree = Parse("""{ "type": "attribute", "key": "campaign", "cmp": "contains", "value": "a[b]%" }""");

        var compiled = NewCompiler().Compile(tree, TenantId);

        compiled.Sql.Should().Contain("JSON_VALUE(p.Attributes, '$.campaign') LIKE '%' + @p0 + '%' ESCAPE '\\'");
        compiled.Parameters.Should().ContainSingle(p => p.Name == "@p0" && p.Kind == SqlValueKind.Text && (string)p.Value == "a\\[b]\\%");
    }

    [Fact]
    public void Minor1_audit_text_doubles_embedded_single_quotes()
    {
        var tree = Parse("""{ "type": "attribute", "key": "tier", "cmp": "eq", "value": "O'Brien" }""");

        var compiled = NewCompiler().Compile(tree, TenantId);

        compiled.ToAuditText().Should().Contain("'O''Brien'");
    }

    [Fact]
    public void UT039_compilation_is_deterministic_across_key_orders()
    {
        const string orderA = """{ "type": "attribute", "key": "plan", "cmp": "eq", "value": "pro" }""";
        const string orderB = """{ "value": "pro", "cmp": "eq", "key": "plan", "type": "attribute" }""";

        var treeA = RuleParser.Parse(JsonDocument.Parse(orderA).RootElement).Tree!;
        var treeB = RuleParser.Parse(JsonDocument.Parse(orderB).RootElement).Tree!;

        var compiledA = NewCompiler().Compile(treeA, TenantId);
        var compiledB = NewCompiler().Compile(treeB, TenantId);

        compiledA.ToAuditText().Should().Be(compiledB.ToAuditText());
        DefinitionHash.ToHex(DefinitionHash.Compute(treeA)).Should().Be(DefinitionHash.ToHex(DefinitionHash.Compute(treeB)));
    }
}
