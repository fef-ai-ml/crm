using System.Data;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using Crm.Kernel.Time;
using Crm.Segmentation.Application.Compilation;
using Crm.Segmentation.Application.Realtime;
using Crm.Segmentation.Domain.Rules;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Crm.IntegrationTests.Segments;

/// <summary>
/// IT-043 (plan I, ruling I1): the real-time evaluator and the compiled SQL give the same answer. A tenant of
/// crafted profiles — one per (key, edge-case value), plus one with no attributes — is written straight into
/// `profiles.Profiles`; every attribute-only, real-time-eligible rule (each such valid fixture plus the extra rules
/// below) is compiled and executed by SQL Server and evaluated in C# over each profile's stored `Attributes`, and
/// the two member sets must be identical. The values are chosen at the seams: case, trailing and leading spaces,
/// numeric strings `TRY_CONVERT` accepts or refuses, booleans, JSON null/object/array, LIKE metacharacters, the
/// 256-character promoted column and the 4,000-character `JSON_VALUE` limit.
/// </summary>
[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class RealtimeParityTests(CrmStackFixture stack) : IAsyncLifetime
{
    private static readonly string[] FixtureNames =
    [
        "attr-eq", "attr-neq", "attr-in", "attr-nin", "attr-gt", "attr-gte", "attr-lt", "attr-lte",
        "attr-contains", "attr-exists", "attr-not-exists", "attr-bool", "depth-5", "seed-no-plan", "seed-high-ltv-sg",
    ];

    private static readonly string LongPlan = new string('a', 256) + "tail";

    private static readonly (string Key, string Json)[] Values =
    [
        ("plan", "\"pro\""), ("plan", "\"PRO\""), ("plan", "\"pro  \""), ("plan", "\"  pro\""), ("plan", "\"pró\""),
        ("plan", "\"free\""), ("plan", "null"), ("plan", "{}"), ("plan", "[\"pro\"]"), ("plan", "\"\""),
        ("plan", JsonSerializer.Serialize(LongPlan)),
        ("country", "\"sg\""), ("country", "\"ID \""), ("country", "\"US\""), ("country", "\"S G\""),
        ("tier", "\"gold\""), ("tier", "\"GOLD\""), ("tier", "\"silver\""), ("tier", "\"\""), ("tier", "null"), ("tier", "[]"), ("tier", "0"),
        ("ltv", "150"), ("ltv", "150.0"), ("ltv", "1.5e2"), ("ltv", "\"150\""), ("ltv", "\" 150 \""), ("ltv", "\"\""), ("ltv", "\"   \""),
        ("ltv", "\"+5\""), ("ltv", "\"5.\""), ("ltv", "\".5\""), ("ltv", "\"-0.5\""), ("ltv", "\"1,000\""), ("ltv", "\"0x10\""),
        ("ltv", "\"NaN\""), ("ltv", "\"Infinity\""), ("ltv", "\"12abc\""), ("ltv", "true"), ("ltv", "50.5"), ("ltv", "100"), ("ltv", "-3"),
        ("campaign", "\"Summer 50%_off\\\\ sale\""), ("campaign", "\"50%_OFF\\\\\""), ("campaign", "\"50X_off\\\\\""), ("campaign", "\"50%Xoff\\\\\""),
        ("opted_in", "true"), ("opted_in", "false"), ("opted_in", "\"TRUE\""), ("opted_in", "\"true \""), ("opted_in", "1"), ("opted_in", "\"yes\""),
        ("note", JsonSerializer.Serialize(new string('n', 4001))), ("note", JsonSerializer.Serialize(new string('n', 4000))),
    ];

    private static readonly string[] ExtraRules =
    [
        """{"type":"attribute","key":"ltv","cmp":"eq","value":0}""",
        """{"type":"attribute","key":"ltv","cmp":"eq","value":150}""",
        """{"type":"attribute","key":"ltv","cmp":"neq","value":150}""",
        """{"type":"attribute","key":"ltv","cmp":"in","value":[0,5,0.5]}""",
        """{"type":"attribute","key":"ltv","cmp":"nin","value":[150,-3]}""",
        """{"type":"attribute","key":"ltv","cmp":"lt","value":0}""",
        """{"type":"attribute","key":"ltv","cmp":"eq","value":"150"}""",
        """{"type":"attribute","key":"ltv","cmp":"eq","value":"150.0"}""",
        """{"type":"attribute","key":"ltv","cmp":"contains","value":"5"}""",
        """{"type":"attribute","key":"plan","cmp":"eq","value":"PRO "}""",
        """{"type":"attribute","key":"plan","cmp":"neq","value":"pro"}""",
        """{"type":"attribute","key":"plan","cmp":"in","value":["free","pro"]}""",
        """{"type":"attribute","key":"plan","cmp":"contains","value":"RO"}""",
        """{"type":"attribute","key":"plan","cmp":"exists"}""",
        """{"type":"attribute","key":"plan","cmp":"eq","value":""}""",
        """{"type":"attribute","key":"country","cmp":"nin","value":["sg","id"]}""",
        """{"type":"attribute","key":"tier","cmp":"eq","value":0}""",
        """{"type":"attribute","key":"tier","cmp":"contains","value":""}""",
        """{"type":"attribute","key":"opted_in","cmp":"eq","value":false}""",
        """{"type":"attribute","key":"opted_in","cmp":"neq","value":true}""",
        """{"type":"attribute","key":"note","cmp":"exists"}""",
        """{"type":"attribute","key":"note","cmp":"not_exists"}""",
        """{"op":"or","children":[{"type":"attribute","key":"tier","cmp":"exists"},{"type":"attribute","key":"ltv","cmp":"gte","value":100}]}""",
    ];

    private readonly Guid _tenantId = Guid.NewGuid();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => SeedDataset.DeleteAsync(stack.SqlConnectionString, _tenantId);

    [Fact]
    public async Task IT043_evaluator_and_compiled_sql_select_the_same_profiles()
    {
        var profiles = await CraftProfilesAsync();
        var rules = FixtureNames.Select(name => (Name: name, Definition: RuleFixtures.LoadDefinition($"{name}.json")))
            .Concat(ExtraRules.Select(json => (Name: json, Definition: Parse(json))))
            .ToList();
        var compiler = new RuleCompiler(new SystemClock());

        var mismatches = new List<string>();
        foreach (var (name, definition) in rules)
        {
            var parsed = RuleParser.Parse(definition);
            parsed.IsValid.Should().BeTrue(name);
            RealtimeEligibility.Classify(parsed.Tree!).IsEligible.Should().BeTrue($"{name} is attribute-only");

            var sql = await ExecuteAsync(compiler.Compile(parsed.Tree!, _tenantId));
            var evaluated = profiles
                .Where(p => RealtimeEvaluator.Evaluate(parsed.Tree!, p.Attributes, _ => 0))
                .Select(p => p.Id)
                .ToHashSet();

            foreach (var profile in profiles.Where(p => sql.Contains(p.Id) != evaluated.Contains(p.Id)))
            {
                mismatches.Add($"{name} on {profile.Json}: SQL {sql.Contains(profile.Id)}, evaluator {evaluated.Contains(profile.Id)}");
            }
        }

        mismatches.Should().BeEmpty();
    }

    private async Task<IReadOnlyList<(Guid Id, string Json, JsonElement Attributes)>> CraftProfilesAsync()
    {
        var documents = Values.Select(v => $"{{\"{v.Key}\":{v.Json}}}").Prepend("{}").ToList();
        var profiles = new List<(Guid, string, JsonElement)>();
        for (var i = 0; i < documents.Count; i++)
        {
            var id = await ProfilesDb.InsertProfileAsync(stack.SqlConnectionString, _tenantId, $"parity-{i}", DateTime.UtcNow);
            await ProfilesDb.SetAttributesAsync(stack.SqlConnectionString, _tenantId, id, documents[i]);
            profiles.Add((id, documents[i].Length > 80 ? documents[i][..80] + "…" : documents[i], Parse(documents[i])));
        }

        return profiles;
    }

    private async Task<HashSet<Guid>> ExecuteAsync(CompiledQuery query)
    {
        await using var connection = new SqlConnection(stack.SqlConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // compiled SQL: every value is a parameter (ADR-0009), the text is the compiler's own
        command.CommandText = query.Sql;
#pragma warning restore CA2100
        foreach (var parameter in query.Parameters)
        {
            command.Parameters.Add(parameter.Kind switch
            {
                SqlValueKind.Text => new SqlParameter(parameter.Name, SqlDbType.NVarChar, 4000) { Value = parameter.Value },
                SqlValueKind.Float => new SqlParameter(parameter.Name, SqlDbType.Float) { Value = parameter.Value },
                SqlValueKind.Int => new SqlParameter(parameter.Name, SqlDbType.Int) { Value = parameter.Value },
                SqlValueKind.DateTime => new SqlParameter(parameter.Name, SqlDbType.DateTime2) { Value = parameter.Value },
                SqlValueKind.Guid => new SqlParameter(parameter.Name, SqlDbType.UniqueIdentifier) { Value = parameter.Value },
                _ => throw new NotSupportedException(parameter.Kind.ToString()),
            });
        }

        var ids = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
