using System.Text.RegularExpressions;
using Crm.Segmentation.Application.Compilation;
using Crm.Segmentation.Domain.Rules;
using FluentAssertions;
using Xunit;

namespace Crm.Segmentation.UnitTests;

/// <summary>Golden-file theory over every valid rule fixture (docs/test/fixtures.md "Golden SQL Files"; UT-021…034,
/// 038). Set `UPDATE_GOLDEN=1` to regenerate deliberately: the test then rewrites the file and fails with
/// "golden updated — review the diff" so the environment variable is never left set by accident.</summary>
[Trait("Category", "Unit")]
public class RuleCompilerGoldenTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime Clock = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [MemberData(nameof(Fixtures.Valid), MemberType = typeof(Fixtures))]
    public void golden_sql_matches_fixture(string name)
    {
        var (definition, _) = Fixtures.Load(name);
        var tree = RuleParser.Parse(definition).Tree!;
        var compiler = new RuleCompiler(new FakeClock(Clock));

        var actual = Normalize(compiler.Compile(tree, TenantId).ToAuditText());
        var path = Path.Combine(Fixtures.GoldenRoot(), $"{name}.sql");

        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(path, actual);
            Assert.Fail("golden updated — review the diff");
        }

        File.Exists(path).Should().BeTrue($"golden file '{path}' is missing; run with UPDATE_GOLDEN=1 to create it");
        Normalize(File.ReadAllText(path)).Should().Be(actual, $"golden file for '{name}'");
    }

    // Named facts for the exact predicate text quoted in docs/test/unit.md UT-021…034, 038.

    [Fact]
    public void UT021_attribute_eq_uses_the_promoted_column()
    {
        var compiled = Compile("attr-eq");

        compiled.Sql.Should().Contain("p.Attr_plan = @p0");
        compiled.Parameters.Should().ContainSingle(p => p.Name == "@p0" && p.Kind == SqlValueKind.Text && (string)p.Value == "pro");
    }

    [Fact]
    public void UT022_attribute_neq_includes_missing_key()
    {
        var compiled = Compile("attr-neq");

        compiled.Sql.Should().Contain("(JSON_VALUE(p.Attributes, '$.tier') <> @p0 OR JSON_VALUE(p.Attributes, '$.tier') IS NULL)");
    }

    [Fact]
    public void UT023_attribute_in_lists_one_parameter_per_element()
    {
        var compiled = Compile("attr-in");

        compiled.Sql.Should().Contain("p.Attr_country IN (@p0, @p1)");
        compiled.Parameters.Should().Contain(p => p.Name == "@p0" && (string)p.Value == "ID");
        compiled.Parameters.Should().Contain(p => p.Name == "@p1" && (string)p.Value == "SG");
    }

    [Fact]
    public void UT024_attribute_nin_includes_missing_key()
    {
        var compiled = Compile("attr-nin");
        var jv = "JSON_VALUE(p.Attributes, '$.tier')";

        compiled.Sql.Should().Contain($"({jv} NOT IN (@p0, @p1) OR {jv} IS NULL)");
    }

    [Theory]
    [InlineData("attr-gt", ">")]
    [InlineData("attr-gte", ">=")]
    [InlineData("attr-lt", "<")]
    [InlineData("attr-lte", "<=")]
    public void UT025_numeric_comparators_use_try_convert(string fixtureName, string op)
    {
        var compiled = Compile(fixtureName);

        compiled.Sql.Should().Contain($"TRY_CONVERT(float, JSON_VALUE(p.Attributes, '$.ltv')) {op} @p0");
        compiled.Parameters.Should().ContainSingle(p => p.Name == "@p0" && p.Kind == SqlValueKind.Float);
    }

    [Fact]
    public void UT026_contains_escapes_like_wildcards()
    {
        var compiled = Compile("attr-contains");
        var jv = "JSON_VALUE(p.Attributes, '$.campaign')";

        compiled.Sql.Should().Contain($"{jv} LIKE '%' + @p0 + '%' ESCAPE '\\'");
        compiled.Parameters.Should().ContainSingle(p => p.Name == "@p0" && (string)p.Value == "50\\%\\_off\\\\");
    }

    [Fact]
    public void UT027_attribute_exists_emits_no_parameter()
    {
        var compiled = Compile("attr-exists");

        compiled.Sql.Should().Contain("JSON_VALUE(p.Attributes, '$.tier') IS NOT NULL");
        compiled.Parameters.Should().ContainSingle("only @tenant, no `@p…` parameter for the leaf itself")
            .Which.Name.Should().Be("@tenant");
    }

    [Fact]
    public void UT028_attribute_not_exists()
    {
        var compiled = Compile("attr-not-exists");

        compiled.Sql.Should().Contain("JSON_VALUE(p.Attributes, '$.tier') IS NULL");
    }

    [Fact]
    public void UT029_event_gte_n()
    {
        var compiled = Compile("event-gte");

        compiled.Sql.Should().Contain(
            "EXISTS (SELECT 1 FROM profiles.Events e WHERE e.TenantId = @tenant AND e.ProfileId = p.Id AND e.Name = @p0 AND e.OccurredAt >= @p1 HAVING COUNT(*) >= @p2)");
        compiled.Parameters.Should().Contain(p => p.Name == "@p1" && (DateTime)p.Value == new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc));
        compiled.Parameters.Should().Contain(p => p.Name == "@p2" && (int)p.Value == 3);
    }

    [Fact]
    public void UT030_event_eq_zero_compiles_to_not_exists_with_no_having()
    {
        var compiled = Compile("event-eq-zero");

        compiled.Sql.Should().Contain("NOT EXISTS (SELECT 1 FROM profiles.Events e WHERE e.TenantId = @tenant AND e.ProfileId = p.Id AND e.Name = @p0 AND e.OccurredAt >= @p1)");
        compiled.Sql.Should().NotContain("HAVING");
    }

    [Fact]
    public void UT031_event_eq_n()
    {
        var compiled = Compile("event-eq-n");

        compiled.Sql.Should().Contain("HAVING COUNT(*) = @p2)");
        compiled.Sql.Should().Contain("EXISTS (SELECT 1 FROM profiles.Events");
        compiled.Sql.Should().NotContain("NOT EXISTS");
    }

    [Fact]
    public void UT032_event_lte_n()
    {
        var compiled = Compile("event-lte");

        compiled.Sql.Should().Contain("NOT EXISTS (SELECT 1 FROM profiles.Events e WHERE e.TenantId = @tenant AND e.ProfileId = p.Id AND e.Name = @p0 AND e.OccurredAt >= @p1 HAVING COUNT(*) > @p2)");
    }

    [Fact]
    public void UT033_nested_and_or_groups()
    {
        var compiled = Compile("group-nested");

        compiled.Sql.Should().Contain("AND");
        compiled.Sql.Should().Contain(" OR ");
        compiled.Parameters.Select(p => p.Name).Should().Equal("@tenant", "@p0", "@p1", "@p2", "@p3", "@p4", "@p5", "@p6", "@p7");
    }

    [Fact]
    public void UT034_not_group()
    {
        var compiled = Compile("group-not");

        compiled.Sql.Should().Contain("NOT (p.Attr_plan = @p0)");
    }

    [Fact]
    public void UT038_booleans_compare_as_strings()
    {
        var compiled = Compile("attr-bool");
        var jv = "JSON_VALUE(p.Attributes, '$.opted_in')";

        compiled.Sql.Should().Contain($"{jv} = @p0");
        compiled.Parameters.Should().ContainSingle(p => p.Name == "@p0" && p.Kind == SqlValueKind.Text && (string)p.Value == "true");
    }

    private static CompiledQuery Compile(string fixtureName)
    {
        var (definition, _) = Fixtures.Load(fixtureName);
        var tree = RuleParser.Parse(definition).Tree!;
        return new RuleCompiler(new FakeClock(Clock)).Compile(tree, TenantId);
    }

    private static string Normalize(string text) =>
        string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Select(line => Regex.Replace(line.TrimEnd(), @"\s+", " ")));
}
