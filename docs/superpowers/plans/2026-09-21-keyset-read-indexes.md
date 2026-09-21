# Keyset Read Indexes Implementation Plan (plan G)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the keyset-paged reads of plan F cost O(page) instead of O(matching rows): an index that serves the profile browse/search order without a sort, a seekable cursor predicate, a total-order clustered key for the timeline, partition-level lock escalation on `profiles.Events` — each proven by a test that reads the real execution plan — so the Segmentation plan inherits an index pattern, not just a query shape.

**Architecture:** Plan F's measurement (final review I6/I7/H1, confirmed on the seeded stack) showed browse/search = clustered scan + sort and timeline = seek over every partition + Top-N sort. This plan (1) tags the two list queries with `TagWith` so their cached plans can be found, (2) adds a test helper that fetches the cached plan of the statement the API really executed (`sys.dm_exec_query_stats` → `sys.dm_exec_query_plan`) and asserts on its operators, (3) replaces `IX_Profiles_TenantId_LastSeenAt` with a filtered, descending, covering keyset index, (4) rewrites both cursor predicates into a seekable form, (5) makes the Events clustered index unique on `(TenantId, ProfileId, OccurredAt, Id)` and sets `LOCK_ESCALATION = AUTO`. Whether SQL Server then drops the timeline's sort is an empirical question; Task 2 carries a pre-made decision rule for both outcomes.

**Tech Stack:** EF Core 10 migrations (SQL Server 2022; the Events part hand-edited because the table is partitioned), `TagWith`, DMVs `sys.dm_exec_query_stats` / `sys.dm_exec_sql_text` / `sys.dm_exec_query_plan`, showplan XML via `System.Xml.Linq`, xUnit + FluentAssertions 7 + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-09-15-crm-slice1-design.md` (§3.1 indexes, "Rulings (plan F)" — keyset follow-up), `docs/data-dictionary/profiles.md` (index tables, "Read paths"), plan F final review findings I6, I7, I9, H1 (summarised in `docs/superpowers/plans/2026-09-21-profiles-read-api.md` and the spec's plan F rulings).

## Global Constraints

- Work on `main`; one commit per task; every commit message ends with a blank line and EXACTLY `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` — never your own model name; check `git log -1 --format=%B` after committing. Commits are SSH-signed through 1Password: never pass `--no-gpg-sign` or `-c commit.gpgsign=false`; if `git commit` fails or hangs ~60 s, leave everything staged and report `DONE_UNCOMMITTED`; do not retry in a loop.
- `dotnet build Crm.sln -warnaserror` → 0 warnings. Known build-error classes: CS1573, CA1873, CA2100 (SQL text built from strings — suppress locally with a justification only where a statement cannot be parameterised), `[Obsolete]`. No `.ConfigureAwait(false)`. Test files need an explicit `using Xunit;`.
- Prefix **every** `dotnet` command with `DOTNET_SYSTEM_NET_DISABLEIPV6=1`. One `dotnet` command at a time, always in the FOREGROUND (never `run_in_background`, never a Monitor), never `rm -rf obj bin`.
- Integration tests: every shell call exports `DOCKER_HOST=unix://$HOME/.local/share/containers/home-lab.sock TESTCONTAINERS_HOST_OVERRIDE=home-lab TESTCONTAINERS_RYUK_DISABLED=true MSSQL_IMAGE=mcr.microsoft.com/mssql/server:2022-latest`. "Cannot connect to the Docker daemon" → report `BLOCKED`.
- Forbidden for implementers and reviewers: reading or editing `deploy/.env`, `.env.local`, `~/.config`, `~/.local`; `podman machine`, `docker context …`, `make up*`, `make down`, `make tunnel*`, `make seed`, `make migrate`, `pkill`, volume removal, prune, `coldstart.sh`; any statement against the compose stack's database; `git stash/restore/reset/checkout --/clean`; committing `.superpowers/`, `.env.local` or `mise.toml`; `curl`/`wget`.
- Migrations: generate with `DOTNET_SYSTEM_NET_DISABLEIPV6=1 dotnet ef migrations add <Name> --project src/Modules/Profiles/Profiles.Infrastructure --startup-project src/Modules/Profiles/Profiles.Infrastructure --context ProfilesDbContext` (the design-time factory needs no database). Never edit `20260920104202_InitialProfiles.cs`. Every index on `profiles.Events` is created `ON PS_Events_Monthly (OccurredAt)` — EF cannot express that, so those operations are raw `migrationBuilder.Sql(...)` and the scaffolded `DropIndex`/`CreateIndex` calls for them are deleted; the model snapshot keeps what the scaffold wrote. `Down` restores the previous state exactly.
- Behaviour of the three read endpoints does not change: same rows, same order, same cursors (`KeysetCursor` format untouched), same errors. The whole integration suite stays green, in particular the tie tests `IT012_pages_are_50_50_20…`, `IT011_profiles_sharing_one_lastSeenAt_are_paged_exactly_once` and `IT007`.
- `docs/api/openapi.json` must not change (`DOTNET_SYSTEM_NET_DISABLEIPV6=1 make openapi` → empty diff). `make docs-check` → 0 errors.
- Where code in this plan disagrees with an existing signature in the tree, the tree wins; adapt and report. Where it disagrees with a ruling below, the ruling wins.

## Rulings

| Id | Ruling | Why | Cost if wrong |
|---|---|---|---|
| G1 | Plans are asserted on what SQL Server actually compiled for the API's own statement: the list queries get `TagWith("profiles-search")` / `TagWith("profiles-timeline")`, tests call the HTTP endpoint and then read the newest cached plan whose text contains the tag. No hand-written copies of the SQL, no `SHOWPLAN` of a reconstruction. | A reconstructed statement (local variables, literal TOP) compiles differently from EF's parameterised one. The tag also helps production diagnostics. | The DMV needs `VIEW SERVER STATE`; tests run as `sa`. |
| G2 | New index `IX_Profiles_TenantId_LastSeenAt_Id` on `(TenantId, LastSeenAt DESC, Id DESC) INCLUDE (ExternalId, Email, Phone, EventCount) WHERE MergedIntoId IS NULL` replaces `IX_Profiles_TenantId_LastSeenAt`. | Serves filter, order, tiebreak and projection of browse and search: ordered scan, no sort, no lookups. The old index has no other reader. | Wider index rows on a column that changes with every event (the old index already paid the key move; the INCLUDEs add `EventCount`). |
| G3 | Cursor predicates become seekable: `sort <= @at AND (sort < @at OR Id < @id)` instead of `sort < @at OR (sort = @at AND Id < @id)`. Same rows. | The OR form cannot seek; page k would scan k pages of the index. | None: the tie tests pin the semantics. |
| G4 | `q` stays a residual predicate on the ordered index scan (the three contact columns are INCLUDEd). A selective `q` on a huge tenant scans until it has a page; accepted for slice 1 and documented. | Seeking the three filtered unique indexes instead would bring the sort back. | Rare-match searches on very large tenants are slow; a dedicated search path is a later decision. |
| G5 | Events clustered index becomes UNIQUE on `(TenantId, ProfileId, OccurredAt, Id)`, renamed `CX_Events_TenantId_ProfileId_OccurredAt_Id`, rebuilt once with `DROP_EXISTING = ON` and renamed with `sp_rename`. | A total-order key that matches `ORDER BY OccurredAt DESC, Id DESC` (backward scan); removes the hidden uniquifier. | Nonclustered indexes on Events carry 16 more bytes per row. |
| G6 | Decision rule for G5, settled by Task 2's plan test on a timeline restricted to ONE month (`from`/`to` inside the current month → one partition): **no Sort operator → keep G5.** Sort still present → G5 buys nothing: take the clustered-key change out of the migration and the model (keep G7), say so in the report and in the data dictionary. For the unrestricted (multi-partition) timeline the test asserts a clustered index SEEK and records whether a Sort remains; the data dictionary states the measured behaviour. | Whether the optimizer treats partition order as `OccurredAt` order is version-dependent; decide by measurement, not by belief (plan E lesson). | One migration rewrite inside Task 2. |
| G7 | `ALTER TABLE profiles.Events SET (LOCK_ESCALATION = AUTO)`. | Standard for partitioned tables: escalation goes to the partition, not the table (plan F I9); helps ingest under load, not only the seeder. | None known. |
| G8 | No change to `UX_Events_Id`, `IX_Events_TenantId_Name_OccurredAt`, the partition function, or the Worker's raw statements. The `name` filter stays a residual predicate on the profile's range. | Out of scope; the name index is keyed for Segmentation's tenant-wide counts. | — |

## File map

| Path | Responsibility |
|---|---|
| `src/Modules/Profiles/Profiles.Infrastructure/Queries/EfProfileQueries.cs` | tags, seekable cursor predicates |
| `src/Modules/Profiles/Profiles.Infrastructure/Persistence/ProfileConfiguration.cs`, `EventRecordConfiguration.cs` | index model |
| `src/Modules/Profiles/Profiles.Infrastructure/Migrations/*_ProfilesKeysetIndex.cs`, `*_EventsKeysetKey.cs` (+ designer files, snapshot) | schema |
| `tests/Crm.IntegrationTests/Fixtures/QueryPlans.cs` | cached-plan lookup + showplan assertions |
| `tests/Crm.IntegrationTests/Fixtures/SeedDataset.cs` | `DeleteAsync` (moved out of `SeedDatasetTests`) |
| `tests/Crm.IntegrationTests/Profiles/ReadPathPlanTests.cs` | the plan tests |
| `tests/Crm.IntegrationTests/Profiles/SchemaTests.cs` | index lists, lock escalation |
| `docs/data-dictionary/profiles.md`, `docs/superpowers/specs/2026-09-15-crm-slice1-design.md` | index tables, read paths, rulings |

---

### Task 1: Plan-test harness, profile keyset index, seekable cursor predicates

**Files:**
- Create: `tests/Crm.IntegrationTests/Fixtures/QueryPlans.cs`, `tests/Crm.IntegrationTests/Profiles/ReadPathPlanTests.cs`, migration `ProfilesKeysetIndex` (three generated files)
- Modify: `EfProfileQueries.cs`, `ProfileConfiguration.cs`, `tests/Crm.IntegrationTests/Fixtures/SeedDataset.cs`, `tests/Crm.IntegrationTests/Profiles/SeedDatasetTests.cs` (use the shared delete), `tests/Crm.IntegrationTests/Profiles/SchemaTests.cs`, `docs/data-dictionary/profiles.md`

**Interfaces:**
- Consumes: `SeedDataset.LoadAsync(CrmApiFactory, Guid)`, `ProfilesClient.ReadAsync<T>`, `TenancySeed`, `CrmStackFixture.SqlConnectionString`, `ProfileListResponse`, `TimelineResponse`.
- Produces: `QueryPlans.LatestAsync(string connectionString, string tag)` → `Task<QueryPlan>`; `QueryPlan.PhysicalOperators` (`IReadOnlyList<string>`), `QueryPlan.Indexes` (`IReadOnlyList<string>`, bracket-free names), `QueryPlan.HasSort`, `QueryPlan.Xml`; `SeedDataset.DeleteAsync(string connectionString, Guid tenantId)`; query tags `profiles-search`, `profiles-timeline` (Task 2 uses the second).

- [ ] **Step 1: Test helper**

`tests/Crm.IntegrationTests/Fixtures/QueryPlans.cs`:

```csharp
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>
/// The plan SQL Server compiled for a statement the API really executed (ruling G1): the statement is found in the plan
/// cache by the comment EF Core's TagWith puts in front of it. Needs VIEW SERVER STATE — the test container runs as sa.
/// </summary>
internal sealed class QueryPlan(string xml)
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
    private readonly XDocument _document = XDocument.Parse(xml);

    public string Xml { get; } = xml;

    public IReadOnlyList<string> PhysicalOperators
        => _document.Descendants(Ns + "RelOp").Select(op => (string)op.Attribute("PhysicalOp")!).ToList();

    /// <summary>Index names the plan touches, without brackets.</summary>
    public IReadOnlyList<string> Indexes
        => _document.Descendants(Ns + "Object")
            .Select(o => ((string?)o.Attribute("Index"))?.Trim('[', ']'))
            .Where(name => name is not null).Select(name => name!).Distinct().ToList();

    /// <summary>A Top-N sort is PhysicalOp "Sort" with LogicalOp "TopN Sort"; both count.</summary>
    public bool HasSort => PhysicalOperators.Contains("Sort");
}

internal static class QueryPlans
{
    public static async Task<QueryPlan> LatestAsync(string connectionString, string tag)
    {
        const string sql = """
            SELECT TOP (1) CONVERT(nvarchar(max), qp.query_plan)
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
            CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
            WHERE st.text LIKE @pattern AND st.text NOT LIKE N'%dm_exec_query_stats%' AND qp.query_plan IS NOT NULL
            ORDER BY qs.last_execution_time DESC
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@pattern", $"%-- {tag}%");
        var xml = await command.ExecuteScalarAsync() as string
            ?? throw new InvalidOperationException($"No cached plan for a statement tagged '{tag}'. Was the endpoint called, and does the query carry TagWith(\"{tag}\")?");
        return new QueryPlan(xml);
    }
}
```

`SeedDataset.cs` — add (move the body from `SeedDatasetTests`' cleanup, which then calls this):

```csharp
    /// <summary>Removes a seeded tenant's rows so 105k rows do not stay in the shared database for the rest of the suite.</summary>
    public static async Task DeleteAsync(string connectionString, Guid tenantId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var table in new[] { "profiles.Events", "profiles.UserIdentities", "profiles.Profiles" })
        {
            await using var command = new SqlCommand($"DELETE FROM {table} WHERE TenantId = @t", connection) { CommandTimeout = 120 };
            command.Parameters.AddWithValue("@t", tenantId);
            await command.ExecuteNonQueryAsync();
        }
    }
```

(The table names are compile-time constants, not input; if CA2100 fires in the test project, suppress it on the method with that justification.)

- [ ] **Step 2: The failing plan test**

`tests/Crm.IntegrationTests/Profiles/ReadPathPlanTests.cs` — one test, because the 10-second seed load is the expensive part; `AssertionScope` reports every broken expectation at once:

```csharp
using Crm.IntegrationTests.Fixtures;
using Crm.Profiles.Application.Queries;
using Crm.Tenancy.Domain;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace Crm.IntegrationTests.Profiles;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class ReadPathPlanTests(CrmStackFixture stack) : IAsyncLifetime
{
    private const string SearchIndex = "IX_Profiles_TenantId_LastSeenAt_Id";

    private CrmApiFactory _factory = null!;
    private HttpClient _client = null!;
    private Guid _tenantId;

    public Task InitializeAsync()
    {
        _factory = new CrmApiFactory(stack);
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_tenantId != Guid.Empty)
        {
            await SeedDataset.DeleteAsync(stack.SqlConnectionString, _tenantId);
        }

        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task NFR02_profile_list_pages_are_served_in_index_order_without_a_sort()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        _tenantId = tenant.TenantId;
        await SeedDataset.LoadAsync(_factory, tenant.TenantId);
        var viewer = TenancySeed.AccessToken(_factory, tenant.ViewerUserId, tenant.TenantId, Roles.Viewer);

        using var scope = new AssertionScope();

        var first = await _client.ReadAsync<ProfileListResponse>("/v1/profiles?limit=50", viewer);
        Expect(await QueryPlans.LatestAsync(stack.SqlConnectionString, "profiles-search"), "browse, first page");

        await _client.ReadAsync<ProfileListResponse>($"/v1/profiles?limit=50&cursor={first.NextCursor}", viewer);
        var paged = await QueryPlans.LatestAsync(stack.SqlConnectionString, "profiles-search");
        Expect(paged, "browse, second page");
        paged.PhysicalOperators.Should().Contain("Index Seek", "the cursor predicate must seek into the keyset index (ruling G3)");

        await _client.ReadAsync<ProfileListResponse>("/v1/profiles?q=alice&limit=50", viewer);
        Expect(await QueryPlans.LatestAsync(stack.SqlConnectionString, "profiles-search"), "search q=alice");
    }

    private static void Expect(QueryPlan plan, string what)
    {
        plan.HasSort.Should().BeFalse($"{what}: rows must come out of the index already ordered; operators were {string.Join(" > ", plan.PhysicalOperators)}");
        plan.Indexes.Should().Contain(SearchIndex, $"{what}: the keyset index must serve the query; indexes were {string.Join(", ", plan.Indexes)}");
        plan.Indexes.Should().NotContain("CX_Profiles_TenantId_Id", $"{what}: a covering index needs no lookup into the clustered index");
    }
}
```

Add the tags FIRST so the test can find a plan at all — in `EfProfileQueries.SearchAsync`, on the final query before `.ToListAsync` (tags may sit anywhere in the chain; put them right after `AsNoTracking()`): `.TagWith("profiles-search")`; in `TimelineAsync` on the events query: `.TagWith("profiles-timeline")`. Do not tag the existence check or `GetAsync`.

Run: `… dotnet test tests/Crm.IntegrationTests --filter "FullyQualifiedName~ReadPathPlanTests"` → RED: a Sort is present and the index is `CX_Profiles_TenantId_Id`. Save the output to the log directory named in your dispatch (`task1-red.log`); the operator lists in the failure messages are the "before" evidence.

- [ ] **Step 3: Index and predicates**

`ProfileConfiguration.cs` — replace the `IX_Profiles_TenantId_LastSeenAt` line with:

```csharp
        // Keyset index of GET /v1/profiles (plan G, ruling G2): filter, order, tiebreak and projection in one ordered scan.
        builder.HasIndex(p => new { p.TenantId, p.LastSeenAt, p.Id })
            .IsDescending(false, true, true)
            .IncludeProperties(p => new { p.ExternalId, p.Email, p.Phone, p.EventCount })
            .HasFilter("[MergedIntoId] IS NULL")
            .HasDatabaseName("IX_Profiles_TenantId_LastSeenAt_Id");
```

Generate the migration `ProfilesKeysetIndex`. Expected content: `DropIndex IX_Profiles_TenantId_LastSeenAt`, `CreateIndex IX_Profiles_TenantId_LastSeenAt_Id` with `descending: new[] { false, true, true }`, `filter: "[MergedIntoId] IS NULL"` and the include annotation; `Down` the reverse. No hand edit is needed — `profiles.Profiles` is not partitioned. Read the generated file and check it says exactly that and nothing else (in particular: no operation on `profiles.Events`).

`EfProfileQueries.cs` — the two cursor predicates (ruling G3):

```csharp
            query = query.Where(p => p.LastSeenAt <= at && (p.LastSeenAt < at || p.Id.CompareTo(id) < 0));
```

```csharp
            query = query.Where(e => e.OccurredAt <= at && (e.OccurredAt < at || e.Id.CompareTo(lastId) < 0));
```

Add one comment above the first: the conjunctive form lets SQL Server seek to the cursor position; the disjunctive form it replaces returns the same rows but can only be evaluated as a residual predicate.

- [ ] **Step 4: Schema test and docs**

`SchemaTests.Profiles_has_the_documented_indexes_constraints_and_promoted_columns`: replace the line `"IX_Profiles_TenantId_LastSeenAt|NONCLUSTERED|0|0"` by `"IX_Profiles_TenantId_LastSeenAt_Id|NONCLUSTERED|0|1"` (keep the list sorted the way the query orders it) and add, after the index assertion:

```csharp
        var keyset = await QueryAsync(
            """
            SELECT c.name + '|' + CAST(ic.key_ordinal AS varchar(2)) + '|' + CAST(ic.is_descending_key AS char(1)) + '|' + CAST(ic.is_included_column AS char(1))
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'profiles.Profiles') AND i.name = N'IX_Profiles_TenantId_LastSeenAt_Id'
            ORDER BY ic.is_included_column, ic.key_ordinal, c.name
            """);
        keyset.Should().Equal(
            "TenantId|1|0|0", "LastSeenAt|2|1|0", "Id|3|1|0",
            "Email|0|0|1", "EventCount|0|0|1", "ExternalId|0|0|1", "Phone|0|0|1");
```

`docs/data-dictionary/profiles.md`: in the Profiles index table replace the `IX_Profiles_TenantId_LastSeenAt` row by `| IX_Profiles_TenantId_LastSeenAt_Id | TenantId, LastSeenAt DESC, Id DESC; INCLUDE ExternalId, Email, Phone, EventCount; WHERE MergedIntoId IS NULL | keyset index of GET /v1/profiles: browse and search pages are an ordered scan, no sort, no lookups |`. In "Read paths" rewrite the search sentence: pages are read in index order from `IX_Profiles_TenantId_LastSeenAt_Id`, the cursor seeks to its position, `q` is a residual predicate on that scan (a rare match on a very large tenant scans until a page is full — known limitation, ruling G4); the plan is asserted by `ReadPathPlanTests`. Leave the timeline sentence for Task 2.

- [ ] **Step 5: Green, whole suite, commit**

`ReadPathPlanTests` → green. Then `DOTNET_SYSTEM_NET_DISABLEIPV6=1 dotnet build Crm.sln -warnaserror`; unit suites (`--filter "Category!=Integration"`); the FULL integration suite once, foreground, to a log → green, 0 skipped (the tie tests prove G3 changed no result); `make docs-check`; `make openapi` → empty diff.

If the plan test stays RED after the index exists: read the operator list in the failure message before changing anything. A remaining `Sort` with the new index in use usually means the index's column order/direction differs from the `ORDER BY`; a `Clustered Index Scan` means the optimizer did not pick the index — check the filter text (`[MergedIntoId] IS NULL`) and that the projection is covered. Do not add index hints; report what you found if two attempts do not fix it.

```bash
git add src tests docs
git commit -m "perf(profiles): keyset-covering index and seekable cursors for GET /v1/profiles, asserted on the cached execution plan"
```

---

### Task 2: Timeline — unique clustered key, partition-level lock escalation, measured decision

**Files:**
- Create: migration `EventsKeysetKey` (three generated files, `Up`/`Down` hand-edited)
- Modify: `EventRecordConfiguration.cs`, `tests/Crm.IntegrationTests/Profiles/ReadPathPlanTests.cs`, `tests/Crm.IntegrationTests/Profiles/SchemaTests.cs`, `docs/data-dictionary/profiles.md`, `docs/superpowers/specs/2026-09-15-crm-slice1-design.md`

**Interfaces:**
- Consumes: Task 1's `QueryPlans`, `QueryPlan`, tag `profiles-timeline`, `SeedDataset.DeleteAsync`; `SeedExpectations.PinnedExternalId`.
- Produces: index `CX_Events_TenantId_ProfileId_OccurredAt_Id` (unless ruling G6 says otherwise), `LOCK_ESCALATION = AUTO` on `profiles.Events`.

- [ ] **Step 1: Failing tests**

Add to `ReadPathPlanTests` a second `[Fact]` (its own seeded tenant, deleted in `DisposeAsync` — keep a list of tenant ids instead of the single field):

```csharp
    [Fact]
    public async Task NFR02_timeline_pages_seek_the_profile_range_and_a_one_month_window_needs_no_sort()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        _tenantIds.Add(tenant.TenantId);
        await SeedDataset.LoadAsync(_factory, tenant.TenantId);
        var viewer = TenancySeed.AccessToken(_factory, tenant.ViewerUserId, tenant.TenantId, Roles.Viewer);
        var pinned = (await _client.ReadAsync<ProfileListResponse>("/v1/profiles?q=alice.1%40", viewer)).Items.Single();

        // One partition: a window inside the current month (the pinned profile has a mandatory event younger than 3 days;
        // on the 1st–3rd of a month that event may fall into the previous month — then use the previous month).
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        if (now.Day <= 3)
        {
            monthStart = monthStart.AddMonths(-1);
        }

        var from = Uri.EscapeDataString(monthStart.ToString("O"));
        var to = Uri.EscapeDataString(monthStart.AddMonths(1).ToString("O"));

        using var scope = new AssertionScope();

        var window = await _client.ReadAsync<TimelineResponse>($"/v1/profiles/{pinned.Id}/events?from={from}&to={to}&limit=5", viewer);
        window.Items.Should().NotBeEmpty("the window must contain events or the plan says nothing");
        var oneMonth = await QueryPlans.LatestAsync(stack.SqlConnectionString, "profiles-timeline");
        oneMonth.Indexes.Should().Contain(TimelineIndex);
        oneMonth.PhysicalOperators.Should().Contain("Clustered Index Seek");
        oneMonth.HasSort.Should().BeFalse($"one partition, total-order key: no sort expected (ruling G6); operators were {string.Join(" > ", oneMonth.PhysicalOperators)}");

        var page1 = await _client.ReadAsync<TimelineResponse>($"/v1/profiles/{pinned.Id}/events?limit=50", viewer);
        await _client.ReadAsync<TimelineResponse>($"/v1/profiles/{pinned.Id}/events?limit=50&cursor={page1.NextCursor}", viewer);
        var unrestricted = await QueryPlans.LatestAsync(stack.SqlConnectionString, "profiles-timeline");
        unrestricted.Indexes.Should().Contain(TimelineIndex);
        unrestricted.PhysicalOperators.Should().Contain("Clustered Index Seek").And.NotContain("Clustered Index Scan");
        testOutput.WriteLine($"unrestricted timeline, second page: sort={unrestricted.HasSort}; operators: {string.Join(" > ", unrestricted.PhysicalOperators)}");
    }
```

with `private const string TimelineIndex = "CX_Events_TenantId_ProfileId_OccurredAt_Id";`, a `List<Guid> _tenantIds`, and `ITestOutputHelper testOutput` added to the primary constructor (xUnit injects it). The last line is a measurement, not an assertion (ruling G6): copy what it prints into the report and the data dictionary.

`SchemaTests.Events_and_all_of_its_indexes_live_on_the_monthly_partition_scheme`: first line of the expectation becomes `"CX_Events_TenantId_ProfileId_OccurredAt_Id|PS_Events_Monthly|1"`. New test:

```csharp
    [Fact]
    public async Task Events_escalates_locks_to_the_partition_not_the_table()
        => (await QueryAsync("SELECT lock_escalation_desc COLLATE DATABASE_DEFAULT FROM sys.tables WHERE object_id = OBJECT_ID(N'profiles.Events')"))
            .Should().Equal("AUTO");
```

Run `ReadPathPlanTests` and `SchemaTests` → RED (index name, uniqueness, `TABLE` escalation, Sort). Save as `task2-red.log`.

- [ ] **Step 2: Model and migration**

`EventRecordConfiguration.cs` — replace the clustered index line and extend the class summary (one sentence: the clustered key ends in `Id` so it is unique and matches the timeline's `ORDER BY OccurredAt DESC, Id DESC` as a backward scan):

```csharp
        builder.HasIndex(e => new { e.TenantId, e.ProfileId, e.OccurredAt, e.Id }).IsUnique().IsClustered().HasDatabaseName("CX_Events_TenantId_ProfileId_OccurredAt_Id");
```

Generate the migration `EventsKeysetKey`, then replace the scaffolded body — delete every `DropIndex`/`CreateIndex` it produced for `profiles.Events` — with:

```csharp
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // profiles.Events is partitioned: EF cannot say ON PS_Events_Monthly, so this is SQL. DROP_EXISTING rebuilds the
            // clustered index (and the nonclustered ones that carry its key) once; the rename makes the name match the key.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE CLUSTERED INDEX CX_Events_TenantId_ProfileId_OccurredAt
                    ON profiles.Events (TenantId, ProfileId, OccurredAt, Id)
                    WITH (DROP_EXISTING = ON) ON PS_Events_Monthly (OccurredAt);
                EXEC sp_rename N'profiles.Events.CX_Events_TenantId_ProfileId_OccurredAt', N'CX_Events_TenantId_ProfileId_OccurredAt_Id', N'INDEX';
                ALTER TABLE profiles.Events SET (LOCK_ESCALATION = AUTO);
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE profiles.Events SET (LOCK_ESCALATION = TABLE);
                EXEC sp_rename N'profiles.Events.CX_Events_TenantId_ProfileId_OccurredAt_Id', N'CX_Events_TenantId_ProfileId_OccurredAt', N'INDEX';
                CREATE CLUSTERED INDEX CX_Events_TenantId_ProfileId_OccurredAt
                    ON profiles.Events (TenantId, ProfileId, OccurredAt)
                    WITH (DROP_EXISTING = ON) ON PS_Events_Monthly (OccurredAt);
                """);
        }
```

`(Id, OccurredAt)` is already unique (`UX_Events_Id`), so the wider key cannot hit a duplicate. Check how the initial migration wraps its raw SQL (transaction suppression, `GO`-free batches) and do the same.

- [ ] **Step 3: Run, then apply ruling G6**

Run `SchemaTests`, `ReadPathPlanTests`, `PartitionMaintenanceTests` (a `SPLIT RANGE` must still work with a unique aligned clustered index), `TimelineTests`, `MergeTests`/`MergeConcurrencyTests` (re-pointing events updates `ProfileId`, now part of a unique key).

- One-month timeline plan has **no Sort** → G5 stands. Go on.
- One-month timeline plan **still sorts** → apply G6: restore the previous clustered index line in `EventRecordConfiguration.cs`, remove the migration with `dotnet ef migrations remove …` (same `--project/--context` arguments; it must not touch the database), regenerate `EventsKeysetKey` so that the snapshot matches the restored model, and leave in it only the `LOCK_ESCALATION` statements; in the tests keep the seek assertions and the escalation test, change `TimelineIndex` back to the old name, turn the one-month `HasSort` assertion into a second measurement line; restore the old first line in `SchemaTests`. Report the operator lists.

- [ ] **Step 4: Docs**

`docs/data-dictionary/profiles.md`: Events index table — the clustered row becomes `| CX_Events_TenantId_ProfileId_OccurredAt_Id | TenantId, ProfileId, OccurredAt, Id | unique, clustered, partition-aligned; the timeline's total order (read backwards) |` (or stays, under G6); add under the table: `LOCK_ESCALATION = AUTO` — locks escalate to the month partition, not the table. "Read paths", timeline sentence: state exactly what Task 2 measured — seek on the profile's range; within one month partition no sort / across partitions `<sort present or not>`; the `name` filter is a residual predicate (ruling G8); asserted by `ReadPathPlanTests`. Remove the "covering keyset indexes are a recorded follow-up" sentence.

Spec: after "Rulings (plan F)" add a "Rulings (plan G)" paragraph: plans are asserted on the cached plan of the tagged statement (G1); the profile keyset index and what it costs on the write path (G2); seekable cursor form (G3); `q` as residual predicate and its limit (G4); the Events clustered key decision **as measured** (G5/G6); partition-level lock escalation (G7); the pattern for every later keyset list — Segmentation's members, changes and runs —: a filtered/covering index keyed `(TenantId, <sort> DESC, Id DESC)`, the conjunctive cursor predicate, a `TagWith` tag and a plan test. In the known-limitations table or paragraph replace the plan F sentence about per-page sorting with what remains true.

- [ ] **Step 5: Whole suite, commit**

Build `-warnaserror`; unit suites; FULL integration suite once, foreground, log file → green, 0 skipped; `make docs-check`; `make openapi` → empty diff.

```bash
git add src tests docs
git commit -m "perf(profiles): unique clustered timeline key and partition-level lock escalation on profiles.Events, plans measured"
```

(Under G6 the message becomes `perf(profiles): partition-level lock escalation on profiles.Events; timeline plan measured, clustered key unchanged`.)

---

### Task 3: Live verification (controller-run)

Not dispatched. On the engine (`DOCKER_HOST` exported in every call, overrides on the make command line; `.env.local` is not read; read-only DMV queries only — no ad-hoc DDL against the stack's database):

- [ ] `make down`, `make up …`, `make tunnel-apps`, `make seed` → dataset loaded, six counts exact; API log shows both new migrations applied.
- [ ] Log in as viewer; browse two pages, `q=alice`, two timeline pages of `u-00001`, a one-month timeline window; then read the cached plans for both tags through `docker exec crm-sqlserver-1 … sqlcmd` (the DMV query of `QueryPlans`) and confirm: search plans use `IX_Profiles_TenantId_LastSeenAt_Id` without a Sort; timeline plans seek the clustered index; note sort/no-sort. Compare response times with plan F's (browse 200 rows 70 ms, q=alice 172/77 ms, timeline 101/45 ms through the tunnel).
- [ ] `sys.tables.lock_escalation_desc` = `AUTO`; `make seed` again → `exists`.
- [ ] Track one event for `u-00001` → first timeline item within 2 s (write path unharmed).

## Exit gate

- Build 0 warnings; unit suites green; integration suite green, 0 skipped; tools tests OK; `make docs-check` 0 errors; `make compose-check` OK; `docs/api/openapi.json` unchanged since plan F.
- Final whole-change review (most capable model), one fix wave, one scoped re-review.
- Task 3's live checks pass; CI green after the user pushes.

## Self-review notes

- **Coverage of the plan F follow-ups:** I7/H1 → Task 1 (index, seekable cursor, plan test, documented pattern for Segmentation); I6 → Task 2 (total-order key, measured decision G6); I9's `LOCK_ESCALATION` → Task 2 (G7); docs that over- or under-claim → Tasks 1–2 docs steps state measured behaviour only.
- **Deliberately not done:** a dedicated search path for selective `q` on huge tenants (G4); any change to the `name` index or to the Worker's statements (G8); a snapshot-consistent cursor; rate limiting reads; LT-002.
- **Type consistency check:** `QueryPlans.LatestAsync(connectionString, tag)` and `QueryPlan.{HasSort, Indexes, PhysicalOperators}` are used with those names in both tests; tags `profiles-search` / `profiles-timeline` are identical in `EfProfileQueries` and the tests; `SeedDataset.DeleteAsync(connectionString, tenantId)` is used by `SeedDatasetTests` and `ReadPathPlanTests`; index names are identical in the EF model, the migrations, `SchemaTests`, `ReadPathPlanTests` and the data dictionary.
