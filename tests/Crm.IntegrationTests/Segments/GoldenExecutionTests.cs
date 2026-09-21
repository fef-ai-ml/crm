using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using Crm.IntegrationTests.Profiles;
using Crm.Kernel.Tenancy;
using Crm.Profiles.Application.Seeding;
using Crm.Profiles.Infrastructure.Persistence;
using Crm.Segmentation.Application.Compilation;
using Crm.Segmentation.Application.Segments;
using Crm.Segmentation.Domain.Rules;
using Crm.Segmentation.Infrastructure.Sql;
using Crm.Tenancy.Domain;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Crm.IntegrationTests.Segments;

/// <summary>
/// IT-042: every golden rule fixture actually executes against SQL Server through <see cref="CompiledQueryExecutor"/>
/// (not just compiles), and the six seed segments return the counts docs/test/fixtures.md documents; H18's
/// promoted-column check and the drift guard against Profiles' own promoted-key constant; the
/// <c>POST /v1/dev/seed-segments</c> endpoint (H22). The seed dataset is expensive (~10 s) and every test here
/// only reads it, so — like <c>TimelineTests</c>/<c>BatchRunTests</c> — it loads once per class rather than once
/// per test method, and the endpoint tests reuse the same tenant instead of seeding a second one. This class does
/// not depend on the Kafka pipeline (the dataset loads through `SqlBulkCopy`, bypassing ingestion), so it is safe
/// to run next to RateLimitTests' undrained `events.raw` partition.
/// </summary>
[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class GoldenExecutionTests : IAsyncLifetime
{
    // Segmentation.UnitTests/Fixtures.cs's ValidFileNames, duplicated here because Crm.IntegrationTests does not
    // reference the Segmentation.UnitTests project — every other integration test file in this directory
    // duplicates its own private LoadDefinition helper for the same reason.
    private static readonly IReadOnlyList<string> ValidFixtureNames =
    [
        "attr-eq", "attr-neq", "attr-in", "attr-nin", "attr-gt", "attr-gte", "attr-lt", "attr-lte",
        "attr-contains", "attr-exists", "attr-not-exists", "attr-bool",
        "event-gte", "event-eq-zero", "event-eq-n", "event-lte",
        "group-nested", "group-not", "depth-5", "leaves-20",
        "seed-pro-id", "seed-purchasers", "seed-dormant", "seed-no-plan", "seed-not-pro", "seed-high-ltv-sg",
    ];

    private static readonly TimeSpan ExecutionBudget = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim InitLock = new(1, 1);
    private static SharedFixture? _shared;

    private readonly CrmStackFixture _stack;
    private CrmApiFactory _factory = null!;
    private SeededTenant _tenant = null!;

    public GoldenExecutionTests(CrmStackFixture stack) => _stack = stack;

    public async Task InitializeAsync()
    {
        _factory = new CrmApiFactory(_stack);

        await InitLock.WaitAsync();
        try
        {
            _shared ??= await BuildFixtureAsync(_stack);
        }
        finally
        {
            InitLock.Release();
        }

        _tenant = _shared.Tenant;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static async Task<SharedFixture> BuildFixtureAsync(CrmStackFixture stack)
    {
        using var factory = new CrmApiFactory(stack);
        var tenant = await TenancySeed.CreateTenantAsync(factory);
        await SeedDataset.LoadAsync(factory, tenant.TenantId);
        return new SharedFixture(tenant);
    }

    private sealed record SharedFixture(SeededTenant Tenant);

    public static TheoryData<string> ValidFixtureNameData() => new(ValidFixtureNames);

    [Theory]
    [MemberData(nameof(ValidFixtureNameData))]
    public async Task IT042_every_valid_fixture_compiles_and_executes(string name)
    {
        var count = await CountAsync(name);

        count.Should().BeGreaterThanOrEqualTo(0, $"'{name}' must compile and execute against the seed dataset without error");
    }

    [Fact]
    public async Task IT042_the_six_seed_segments_return_their_documented_counts()
    {
        foreach (var (name, expected) in SeedExpectations.Segments)
        {
            var count = await CountAsync(name);
            count.Should().Be(expected, $"'{name}' must match the count docs/test/fixtures.md documents");
        }
    }

    /// <summary>H18: the promoted attribute columns exist on `profiles.Profiles`, and Segmentation's own constant
    /// (which cannot reference Profiles, per the module boundary) has exactly Profiles' promoted-key set — the
    /// drift guard a reviewer asked for in Task 3.</summary>
    [Fact]
    public async Task H18_promoted_columns_exist_and_segmentations_key_set_matches_profiles()
    {
        PromotedAttributeColumns.Keys.Should().BeEquivalentTo(PromotedAttributes.Keys,
            "Segmentation's promoted-key constant must not drift from Profiles' own, even though it cannot reference it directly");

        await using var connection = new SqlConnection(_stack.SqlConnectionString);
        await connection.OpenAsync();
        foreach (var key in PromotedAttributeColumns.Keys)
        {
            PromotedAttributeColumns.TryGetColumn(key, out var column).Should().BeTrue();
            (await ColumnLengthAsync(connection, column)).Should().NotBeNull($"'{column}' must exist as a computed column on profiles.Profiles");
        }
    }

    /// <summary>H22: as Admin in Development, six segments come back, five with a queued/finished run (the sixth,
    /// `seed-high-ltv-sg`, is Draft); a second call creates and queues nothing new once those runs exist.</summary>
    [Fact]
    public async Task dev_seed_segments_endpoint_upserts_six_segments_and_queues_five_runs()
    {
        await using var devFactory = new CrmApiFactory(_stack) { EnvironmentName = "Development" };
        using var devClient = devFactory.CreateApiClient();
        var admin = TenancySeed.AccessToken(devFactory, _tenant.AdminUserId, _tenant.TenantId, Roles.Admin);

        var first = await SeedSegmentsAsync(devClient, admin);
        first.Should().HaveCount(6);
        first.Select(r => r.Name).Should().BeEquivalentTo(
            ["seed-pro-id", "seed-purchasers", "seed-dormant", "seed-no-plan", "seed-not-pro", "seed-high-ltv-sg"]);

        var draft = first.Single(r => r.Name == "seed-high-ltv-sg");
        draft.Status.Should().Be("Draft");
        draft.RunId.Should().BeNull("a Draft segment is never auto-run");

        var active = first.Where(r => r.Name != "seed-high-ltv-sg").ToList();
        active.Should().OnlyContain(r => r.Status == "Active" && r.RunId != null);

        foreach (var result in active)
        {
            await WaitForFinishedAsync(devClient, admin, result.Id, result.RunId!.Value);
        }

        var second = await SeedSegmentsAsync(devClient, admin);
        second.Select(r => r.Id).Should().BeEquivalentTo(first.Select(r => r.Id), "no new segments are created by a second call");
        second.Should().OnlyContain(r => r.RunId == null, "every Active seed segment already has a finished run; nothing new is queued");
    }

    [Fact]
    public async Task dev_seed_segments_endpoint_is_admin_only_and_404_outside_development()
    {
        await using var devFactory = new CrmApiFactory(_stack) { EnvironmentName = "Development" };
        using var devClient = devFactory.CreateApiClient();
        var viewer = TenancySeed.AccessToken(devFactory, _tenant.ViewerUserId, _tenant.TenantId, Roles.Viewer);

        using var forbidden = await devClient.PostAsync("/v1/dev/seed-segments", viewer, new { });
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var testingClient = _factory.CreateApiClient();
        var admin = TenancySeed.AccessToken(_factory, _tenant.AdminUserId, _tenant.TenantId, Roles.Admin);
        using var notMapped = await testingClient.PostAsync("/v1/dev/seed-segments", admin, new { });
        notMapped.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<List<SeedSegmentDto>> SeedSegmentsAsync(HttpClient client, string bearer)
    {
        using var response = await client.PostAsync("/v1/dev/seed-segments", bearer, new { });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<List<SeedSegmentDto>>())!;
    }

    private static async Task<SegmentRunListItem> WaitForFinishedAsync(HttpClient client, string bearer, Guid segmentId, Guid runId)
        => await Eventually.GetAsync(
            async () =>
            {
                var page = await client.ReadAsync<SegmentRunListResponse>($"/v1/segments/{segmentId}/runs?limit=200", bearer);
                return page.Items.SingleOrDefault(i => i.RunId == runId) is { Status: "Finished" } run ? run : null;
            },
            $"run {runId} finishes");

    private async Task<int> CountAsync(string fixtureName)
    {
        var definition = LoadDefinition(fixtureName);
        var parsed = RuleParser.Parse(definition);
        parsed.IsValid.Should().BeTrue($"'{fixtureName}' is a valid fixture");

        using var scope = _factory.Services.CreateScope();
        ((TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>()).Resolve(_tenant.TenantId, null, TenantPrincipalKind.None);

        var compiler = scope.ServiceProvider.GetRequiredService<RuleCompiler>();
        var executor = scope.ServiceProvider.GetRequiredService<CompiledQueryExecutor>();
        var compiled = compiler.Compile(parsed.Tree!, _tenant.TenantId);

        return await executor.CountAsync(compiled, ExecutionBudget, CancellationToken.None);
    }

    private static async Task<int?> ColumnLengthAsync(SqlConnection connection, string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COL_LENGTH('profiles.Profiles', @column)";
        command.Parameters.Add(new SqlParameter("@column", column));
        var result = await command.ExecuteScalarAsync();
        // COL_LENGTH returns int per T-SQL docs, but the driver maps it to smallint (Int16) in practice here —
        // Convert rather than a direct cast so either representation works.
        return result is null or DBNull ? null : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    /// <summary>Mirrors SegmentCrudTests.LoadDefinition: walks up from the test binary to the directory containing Crm.sln.</summary>
    private static JsonElement LoadDefinition(string fixtureFileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Crm.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new DirectoryNotFoundException("Crm.sln not found above " + AppContext.BaseDirectory);
        }

        var path = Path.Combine(dir.FullName, "tests", "fixtures", "rules", "valid", $"{fixtureFileName}.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone().GetProperty("definition");
    }

    private sealed record SeedSegmentDto(Guid Id, string Name, string Status, Guid? RunId);
}
