using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using Crm.IntegrationTests.Profiles;
using Crm.Segmentation.Application;
using Crm.Segmentation.Application.Segments;
using Crm.Segmentation.Infrastructure.Runs;
using Crm.Tenancy.Domain;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Crm.IntegrationTests.Segments;

/// <summary>IT-016…020 (task-7 brief). Seeds through `SeedDataset.LoadAsync` (bypasses ingestion, safe next to
/// RateLimitTests' undrained partition) and writes attribute changes directly through `ProfilesDb` — never
/// identify → Kafka, since the pipeline is not what is under test. `SegmentHooks` is reset in
/// <see cref="DisposeAsync"/> so a rewrite/pause callback never leaks into another test class.</summary>
[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class BatchRunTests(CrmStackFixture stack) : IAsyncLifetime
{
    private readonly CrmApiFactory _factory = new(stack);
    private readonly List<Guid> _tenantIds = [];
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        stack.SegmentHooks.Reset();
        foreach (var tenantId in _tenantIds)
        {
            await SeedDataset.DeleteAsync(stack.SqlConnectionString, tenantId);
        }

        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IT016_queued_run_is_picked_up_and_materialized()
    {
        var (_, admin) = await SeededTenantAsync();
        using var worker = stack.Worker.CreateClient();
        var runsBefore = await WorkerMetrics.SegmentRunSecondsCountAsync(worker);

        var segmentId = await CreateSegmentAsync(admin, "run-basic", "seed-pro-id.json");

        var runId = await PostRunAsync(segmentId, admin);

        // Fix round 1, Minor 6: the worker polls every 5 s, so "reaches Running promptly" needs margin against
        // the worst case (queued just after a tick fires) plus real network round trips against the remote
        // Testcontainers host — 15 s, not the brief's original 5 s.
        await Eventually.TrueAsync(
            async () => (await FindRunAsync(segmentId, runId, admin)) is { Status: "Running" or "Finished" },
            "the run leaves Queued promptly",
            TimeSpan.FromSeconds(15));

        var finished = await Eventually.GetAsync(
            async () => (await FindRunAsync(segmentId, runId, admin)) is { Status: "Finished" } run ? run : null,
            "the run finishes");
        finished.MemberCount.Should().Be(412);
        finished.Trigger.Should().Be("Manual");

        (await SegmentsDb.CountRunMembersAsync(stack.SqlConnectionString, runId)).Should().Be(412);

        // Fix round 1, Important 2: proves H20 end to end — CompiledSql is the un-rewritten compiled SELECT, not
        // whatever a test hook (or a future regression) might substitute at execution time.
        var compiledSql = await SegmentsDb.GetCompiledSqlAsync(stack.SqlConnectionString, runId);
        compiledSql.Should().StartWith("SELECT p.Id FROM profiles.Profiles p");
        compiledSql.Should().Contain("@tenant = ");

        var segment = await _client.ReadAsync<SegmentResponse>($"/v1/segments/{segmentId}", admin);
        segment.CurrentRunId.Should().Be(runId);
        segment.MemberCount.Should().Be(412);

        (await WorkerMetrics.SegmentRunSecondsCountAsync(worker)).Should().BeGreaterThanOrEqualTo(runsBefore + 1);
    }

    [Fact]
    public async Task IT016_second_run_while_one_is_outstanding_returns_the_existing_runId()
    {
        var (_, admin) = await SeededTenantAsync();
        var segmentId = await CreateSegmentAsync(admin, "run-dedupe", "seed-pro-id.json");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid firstRunId;
        using (stack.SegmentHooks.OnMembersInserted((_, _) =>
        {
            entered.TrySetResult();
            return release.Task;
        }))
        {
            firstRunId = await PostRunAsync(segmentId, admin);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var secondRunId = await PostRunAsync(segmentId, admin);
            secondRunId.Should().Be(firstRunId, "H5: a Queued or Running run for the segment is returned as-is");

            release.TrySetResult();
        }

        await WaitForFinishedAsync(segmentId, firstRunId, admin);
    }

    /// <summary>Fix round 1, Important 1: the happy-path dedupe test above serializes the two POSTs (it waits for
    /// a hook before firing the second one), which never exercises two `EnqueueAsync` calls actually racing each
    /// other before either commits. This test fires 8 real concurrent POSTs instead, with the worker held on the
    /// first one it claims (so nothing reaches Finished mid-burst and none of the tick's later claims fire), and
    /// checks the actual guarantee: `UX_SegmentRuns_TenantId_SegmentId_Active`, not the check-then-insert.</summary>
    [Fact]
    public async Task IT016_eight_concurrent_runs_produce_exactly_one_active_run()
    {
        var (tenant, admin) = await SeededTenantAsync();
        var segmentId = await CreateSegmentAsync(admin, "run-concurrent", "seed-pro-id.json");

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (stack.SegmentHooks.OnMembersInserted((_, _) => release.Task))
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _client.PostAsync($"/v1/segments/{segmentId}/run", admin, new { })));
            try
            {
                responses.Select(r => r.StatusCode).Should().OnlyContain(s => s == HttpStatusCode.Accepted);
                var runIds = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<RunQueuedResponse>())!.RunId));
                runIds.Distinct().Should().ContainSingle("H5: eight concurrent POSTs for one segment must still resolve to one run");

                (await SegmentsDb.ListRunIdsAsync(stack.SqlConnectionString, tenant.TenantId, segmentId))
                    .Should().ContainSingle("only one Queued/Running row may exist for the segment at a time");
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }

            release.TrySetResult();
        }

        var runId = (await SegmentsDb.ListRunIdsAsync(stack.SqlConnectionString, tenant.TenantId, segmentId)).Single();
        await WaitForFinishedAsync(segmentId, runId, admin);
    }

    /// <summary>C11/M-9: `SegmentRunner` compiles the definition at execution time (it re-reads `Segment.Definition`
    /// fresh when the worker claims the run — `SegmentRunner.cs`'s own read happens before the transaction, not
    /// at enqueue time), so a `PUT` that lands while a run is already in flight (past that read) must NOT be
    /// picked up by it — the in-flight run's `CompiledSql` reflects the definition it started with.</summary>
    [Fact]
    public async Task IT_M9_a_run_already_in_flight_uses_the_definition_it_started_with_not_a_concurrent_PUT()
    {
        var (_, admin) = await SeededTenantAsync();
        var segmentId = await CreateSegmentAsync(admin, "run-definition-pin", "seed-purchasers.json");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid runId;
        using (stack.SegmentHooks.OnMembersInserted((_, _) =>
        {
            entered.TrySetResult();
            return release.Task;
        }))
        {
            runId = await PostRunAsync(segmentId, admin);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The run has already read and compiled seed-purchasers.json's definition (past the read, mid
            // transaction) — this PUT must have no effect on it.
            var newDefinition = RuleFixtures.LoadDefinition("seed-no-plan.json");
            using var updated = await _client.PutAsync($"/v1/segments/{segmentId}", admin, new { name = "run-definition-pin", definition = newDefinition, status = "Draft" });
            updated.StatusCode.Should().Be(HttpStatusCode.OK);

            release.TrySetResult();
        }

        await WaitForFinishedAsync(segmentId, runId, admin);

        var compiledSql = await SegmentsDb.GetCompiledSqlAsync(stack.SqlConnectionString, runId);
        compiledSql.Should().Contain("profiles.Events", "the run used seed-purchasers.json, the definition it started with");
        compiledSql.Should().NotContain("Attr_plan IS NULL", "seed-no-plan.json's definition, PUT while the run was in flight, must never reach it");
    }

    [Fact]
    public async Task IT016_archived_segment_run_is_409()
    {
        var (_, admin) = await SeededTenantAsync();
        var segmentId = await CreateSegmentAsync(admin, "run-archived", "seed-pro-id.json");
        (await _client.DeleteAsync($"/v1/segments/{segmentId}", admin)).Dispose();

        using var response = await _client.PostAsync($"/v1/segments/{segmentId}/run", admin, new { });
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("segment.archived");
    }

    [Fact]
    public async Task IT016_unknown_segment_run_is_404()
    {
        var (_, admin) = await SeededTenantAsync();
        using var response = await _client.PostAsync($"/v1/segments/{Guid.NewGuid()}/run", admin, new { });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task IT017_run_diff_records_entered_and_exited()
    {
        var (tenant, admin) = await SeededTenantAsync();
        var segmentId = await CreateSegmentAsync(admin, "run-diff", "seed-pro-id.json");

        var firstRunId = await PostRunAsync(segmentId, admin);
        var firstRun = await WaitForFinishedAsync(segmentId, firstRunId, admin);
        firstRun.MemberCount.Should().Be(412);

        var losers = await TopRunMemberIdsAsync(firstRunId, 3);
        var gainers = await FindOtherwiseQualifyingNonMembersAsync(tenant.TenantId, firstRunId, 5);

        foreach (var loser in losers)
        {
            await ProfilesDb.SetPlanAsync(stack.SqlConnectionString, tenant.TenantId, loser, null);
        }

        foreach (var gainer in gainers)
        {
            await ProfilesDb.SetPlanAsync(stack.SqlConnectionString, tenant.TenantId, gainer, "pro");
        }

        var secondRunId = await PostRunAsync(segmentId, admin);
        var secondRun = await WaitForFinishedAsync(segmentId, secondRunId, admin);
        secondRun.MemberCount.Should().Be(412 - 3 + 5);
        secondRun.FinishedAt.Should().NotBeNull();

        (await SegmentsDb.CountMembershipChangesAsync(stack.SqlConnectionString, tenant.TenantId, segmentId, secondRun.FinishedAt!.Value, "Entered"))
            .Should().Be(5);
        (await SegmentsDb.CountMembershipChangesAsync(stack.SqlConnectionString, tenant.TenantId, segmentId, secondRun.FinishedAt!.Value, "Exited"))
            .Should().Be(3);
    }

    [Fact]
    public async Task IT018_readers_never_see_a_partial_run()
    {
        var (tenant, admin) = await SeededTenantAsync();
        var segmentId = await CreateSegmentAsync(admin, "run-partial", "seed-pro-id.json");

        var firstRunId = await PostRunAsync(segmentId, admin);
        await WaitForFinishedAsync(segmentId, firstRunId, admin);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (stack.SegmentHooks.OnMembersInserted((_, _) =>
        {
            entered.TrySetResult();
            return release.Task;
        }))
        {
            var secondRunId = await PostRunAsync(segmentId, admin);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var state = await SegmentsDb.GetSegmentStateAsync(stack.SqlConnectionString, tenant.TenantId, segmentId);
            state.CurrentRunId.Should().Be(firstRunId, "the transaction has not updated segments.Segments yet");
            state.MemberCount.Should().Be(412);

            var segment = await _client.ReadAsync<SegmentResponse>($"/v1/segments/{segmentId}", admin);
            segment.MemberCount.Should().Be(412);
            segment.CurrentRunId.Should().Be(firstRunId);

            release.TrySetResult();
            await WaitForFinishedAsync(segmentId, secondRunId, admin);
        }
    }

    /// <summary>IT-018's read half (task-8 brief): `GET /segments/{id}/members` follows `Segments.CurrentRunId`,
    /// so while a second run's transaction is open it keeps serving the first run's 412 members — same ids, same
    /// count — and only the released run's members once the transaction commits.</summary>
    [Fact]
    public async Task IT018_members_read_serves_the_previous_run_while_a_run_is_blocked()
    {
        var (_, admin) = await SeededTenantAsync();
        var segmentId = await CreateSegmentAsync(admin, "run-partial-members", "seed-pro-id.json");

        var firstRunId = await PostRunAsync(segmentId, admin);
        await WaitForFinishedAsync(segmentId, firstRunId, admin);
        var firstRunMemberIds = await SegmentsDb.ListRunMemberIdsAsync(stack.SqlConnectionString, firstRunId);
        firstRunMemberIds.Should().HaveCount(412);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (stack.SegmentHooks.OnMembersInserted((_, _) =>
        {
            entered.TrySetResult();
            return release.Task;
        }))
        {
            var secondRunId = await PostRunAsync(segmentId, admin);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var membersWhileBlocked = await AllMemberIdsAsync(segmentId, admin);
            membersWhileBlocked.Should().HaveCount(412);
            membersWhileBlocked.Should().BeEquivalentTo(firstRunMemberIds, "the transaction has not updated segments.Segments yet, so /members still follows the first run");

            release.TrySetResult();
            await WaitForFinishedAsync(segmentId, secondRunId, admin);
        }

        (await AllMemberIdsAsync(segmentId, admin)).Should().HaveCount(412);
    }

    [Fact]
    public async Task IT019_only_the_last_three_runs_are_kept()
    {
        var (tenant, admin) = await SeededTenantAsync();
        var segmentId = await CreateSegmentAsync(admin, "run-prune", "seed-pro-id.json");

        var runIds = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var runId = await PostRunAsync(segmentId, admin);
            await WaitForFinishedAsync(segmentId, runId, admin);
            runIds.Add(runId);
        }

        var remaining = await SegmentsDb.ListRunIdsAsync(stack.SqlConnectionString, tenant.TenantId, segmentId);
        remaining.Should().HaveCount(3);
        remaining.Should().BeEquivalentTo(runIds.TakeLast(3));

        foreach (var runId in runIds.TakeLast(3))
        {
            (await SegmentsDb.CountRunMembersAsync(stack.SqlConnectionString, runId)).Should().Be(412);
        }

        foreach (var runId in runIds.Take(2))
        {
            (await SegmentsDb.CountRunMembersAsync(stack.SqlConnectionString, runId)).Should().Be(0, "the oldest runs and their members are pruned (H8)");
        }
    }

    [Fact]
    public async Task IT020_failed_run_leaves_the_previous_run_current()
    {
        var (tenant, admin) = await SeededTenantAsync();
        var segmentId = await CreateSegmentAsync(admin, "run-failure", "seed-pro-id.json");

        var firstRunId = await PostRunAsync(segmentId, admin);
        await WaitForFinishedAsync(segmentId, firstRunId, admin);

        using var worker = stack.Worker.CreateClient();
        var failedBefore = await WorkerMetrics.SegmentRunSecondsFailedCountAsync(worker);

        using (stack.SegmentHooks.OnRewriteQuery((kind, sql) =>
            kind == SegmentQueryKind.Run ? "SELECT CAST('x' AS uniqueidentifier) AS Id" : sql))
        {
            var secondRunId = await PostRunAsync(segmentId, admin);
            var secondRun = await Eventually.GetAsync(
                async () => (await FindRunAsync(segmentId, secondRunId, admin)) is { Status: "Failed" } run ? run : null,
                "the run fails");

            secondRun.Error.Should().NotBeNullOrEmpty();
            (await SegmentsDb.CountRunMembersAsync(stack.SqlConnectionString, secondRunId)).Should().Be(0);
        }

        var state = await SegmentsDb.GetSegmentStateAsync(stack.SqlConnectionString, tenant.TenantId, segmentId);
        state.CurrentRunId.Should().Be(firstRunId);
        state.MemberCount.Should().Be(412);

        // B3 (I-3): the failed run must leave a distinctly-tagged metric sample, not just a bump to the
        // untagged total. RecordRunDuration is called after RunAsync returns — a moment after the run's row
        // already shows Failed via the API — so this polls rather than asserting once immediately.
        await Eventually.TrueAsync(
            async () => (await WorkerMetrics.SegmentRunSecondsFailedCountAsync(worker)) > failedBefore,
            "the failed run's metric sample lands");
    }

    /// <summary>B2 (I-2, ruling H29): if the run's row leaves `Running` (the reaper declares it
    /// Failed/'abandoned') while its own transaction is still open, the finish update's `AND Status = 'Running'`
    /// guard must find zero rows, so the whole transaction rolls back — nothing of the run survives — and the
    /// reaper's status is never overwritten back to Finished, nor does MarkFailedAsync's own guarded update
    /// clobber 'abandoned'. Mutation-check: fix-wave-mutation.log.</summary>
    [Fact]
    public async Task IT_H29_a_reaped_run_never_resurrects_and_rolls_back_completely()
    {
        var (tenant, admin) = await SeededTenantAsync();
        var segmentId = await CreateSegmentAsync(admin, "run-reaped", "seed-pro-id.json");

        using var worker = stack.Worker.CreateClient();
        var runsBefore = await WorkerMetrics.SegmentRunSecondsCountAsync(worker);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid runId;
        using (stack.SegmentHooks.OnMembersInserted((_, _) =>
        {
            entered.TrySetResult();
            return release.Task;
        }))
        {
            runId = await PostRunAsync(segmentId, admin);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Simulate the reaper firing while this run's transaction is still open (H29): a separate statement
            // against the same row, from outside the transaction, so it commits immediately regardless of what
            // the runner is doing.
            await SegmentsDb.MarkRunAbandonedAsync(stack.SqlConnectionString, runId);
            release.TrySetResult();

            // Waits for RunOneAsync's own metric record, which only happens after RunAsync has fully returned —
            // committed or rolled back — rather than polling the run's row directly: a "Status == Failed" poll
            // would also be (falsely) satisfied by the moment right after MarkRunAbandonedAsync above, before the
            // runner's own (correct or, under a mutation, incorrect) finish attempt has even run.
            await Eventually.TrueAsync(
                async () => (await WorkerMetrics.SegmentRunSecondsCountAsync(worker)) > runsBefore,
                "the runner's own attempt to finish (or fail) the reaped run completes");
        }

        var status = await SegmentsDb.GetRunStatusAsync(stack.SqlConnectionString, runId);
        status.Status.Should().Be("Failed");
        status.Error.Should().Be("abandoned", "MarkFailedAsync's guarded update must never overwrite the reaper's status");

        (await SegmentsDb.CountRunMembersAsync(stack.SqlConnectionString, runId)).Should().Be(0, "the transaction rolled back; no member rows for the reaped run survive");
        (await SegmentsDb.CountMembershipChangesForSegmentAsync(stack.SqlConnectionString, segmentId)).Should().Be(0, "the Entered/Exited inserts rolled back with the rest of the transaction");

        var state = await SegmentsDb.GetSegmentStateAsync(stack.SqlConnectionString, tenant.TenantId, segmentId);
        state.CurrentRunId.Should().BeNull("no run has ever finished for this brand-new segment, and the reaped one must not become it");
        state.MemberCount.Should().Be(0);
    }

    /// <summary>B5 (M-2, ruling H30): a segment archived after its run was queued does not materialise — the
    /// runner itself refuses once it re-reads the segment, rather than relying on the archive endpoint to have
    /// stopped an in-flight run (it does not). Holds the worker's single-threaded drain loop on a DIFFERENT
    /// segment's run (its `MembersInsertedAsync` pauses there) so the target segment's run — posted next — sits
    /// Queued and unclaimed for that whole tick; archiving it then and releasing lets a later tick claim it
    /// fresh, straight into the archived check.</summary>
    [Fact]
    public async Task IT_H30_a_run_whose_segment_is_archived_when_picked_up_does_not_materialize()
    {
        var (tenant, admin) = await SeededTenantAsync();
        var holdSegmentId = await CreateSegmentAsync(admin, "run-hold-worker", "seed-purchasers.json");
        var targetSegmentId = await CreateSegmentAsync(admin, "run-archived-midflight", "seed-pro-id.json");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid targetRunId;
        using (stack.SegmentHooks.OnMembersInserted((_, _) =>
        {
            entered.TrySetResult();
            return release.Task;
        }))
        {
            await PostRunAsync(holdSegmentId, admin);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Queued while the worker's current tick is still busy with holdSegmentId's run — this tick's
            // ReadQueuedAsync already ran before this row existed, so it cannot be claimed until a later tick.
            targetRunId = await PostRunAsync(targetSegmentId, admin);
            (await _client.DeleteAsync($"/v1/segments/{targetSegmentId}", admin)).Dispose();

            release.TrySetResult();
        }

        var failed = await Eventually.GetAsync(
            async () => (await FindRunAsync(targetSegmentId, targetRunId, admin)) is { Status: "Failed" } run ? run : null,
            "a later tick claims the queued run and the runner refuses the now-archived segment");
        failed.Error.Should().Be("segment archived");

        var state = await SegmentsDb.GetSegmentStateAsync(stack.SqlConnectionString, tenant.TenantId, targetSegmentId);
        state.CurrentRunId.Should().BeNull("the refused run never swaps in");
        state.MemberCount.Should().Be(0);
        (await SegmentsDb.CountMembershipChangesForSegmentAsync(stack.SqlConnectionString, targetSegmentId)).Should().Be(0);
    }

    /// <summary>Fix round 1, item 4 (H4, a known gap in the original Task 7); reap window per B2/I-2/H29: with
    /// the default `Segmentation:Run:CommandTimeoutSeconds` (300 s), the cutoff is `max(10 min, 8 × 300 s)` =
    /// 40 minutes, not a flat 10 — a run stuck `Running` past that is abandoned. Calls the worker's reap
    /// directly (exposed the same way `SegmentScheduler.EnqueueScheduledRunsAsync` is) instead of waiting on a
    /// 41-minute clock.</summary>
    [Fact]
    public async Task IT_H4_abandoned_run_is_failed_a_recent_one_is_left_alone_and_a_new_run_can_follow()
    {
        var (tenant, admin) = await SeededTenantAsync();
        var segmentId = await CreateSegmentAsync(admin, "run-abandoned", "seed-pro-id.json");

        var abandonedRunId = await SegmentsDb.InsertRunningSegmentRunAsync(stack.SqlConnectionString, tenant.TenantId, segmentId, DateTime.UtcNow.AddMinutes(-41));

        var worker = stack.Worker.Services.GetServices<IHostedService>().OfType<SegmentRunWorker>().Single();
        await worker.ReapAbandonedRunsAsync(CancellationToken.None);

        var abandoned = await SegmentsDb.GetRunStatusAsync(stack.SqlConnectionString, abandonedRunId);
        abandoned.Status.Should().Be("Failed");
        abandoned.Error.Should().Be("abandoned");
        abandoned.FinishedAt.Should().NotBeNull();

        // With item 1's unique index in place, the abandoned run no longer blocks a new one for this segment.
        var newRunId = await PostRunAsync(segmentId, admin);
        newRunId.Should().NotBe(abandonedRunId);
        await WaitForFinishedAsync(segmentId, newRunId, admin);
    }

    [Fact]
    public async Task IT_H4_a_recent_running_run_is_left_alone_by_the_reap()
    {
        var (tenant, admin) = await SeededTenantAsync();
        var segmentId = await CreateSegmentAsync(admin, "run-not-abandoned", "seed-pro-id.json");

        var recentRunId = await SegmentsDb.InsertRunningSegmentRunAsync(stack.SqlConnectionString, tenant.TenantId, segmentId, DateTime.UtcNow.AddMinutes(-2));

        var worker = stack.Worker.Services.GetServices<IHostedService>().OfType<SegmentRunWorker>().Single();
        await worker.ReapAbandonedRunsAsync(CancellationToken.None);

        (await SegmentsDb.GetRunStatusAsync(stack.SqlConnectionString, recentRunId)).Status.Should().Be("Running", "2 minutes is well under the 40-minute abandon threshold (B2/H29: max(10 min, 8 x 300 s))");
    }

    /// <summary>Fix round 1, item 5 (Minor 4): prune must also run after a Failed run, or a segment that keeps
    /// failing accumulates Failed rows forever. One success (R1, becomes CurrentRunId) then five failures via
    /// IT-020's hook (R2..R6): terminal rows end up being the newest three (R4, R5, R6) plus R1, since R1 is not
    /// among the newest three but is still CurrentRunId — 4 total, R2 and R3 pruned.</summary>
    [Fact]
    public async Task IT_H8_failed_runs_are_pruned_too_current_plus_newest_three_survive()
    {
        var (tenant, admin) = await SeededTenantAsync();
        var segmentId = await CreateSegmentAsync(admin, "run-fail-prune", "seed-pro-id.json");

        var currentRunId = await PostRunAsync(segmentId, admin);
        await WaitForFinishedAsync(segmentId, currentRunId, admin);

        var failedRunIds = new List<Guid>();
        using (stack.SegmentHooks.OnRewriteQuery((kind, sql) =>
            kind == SegmentQueryKind.Run ? "SELECT CAST('x' AS uniqueidentifier) AS Id" : sql))
        {
            for (var i = 0; i < 5; i++)
            {
                var runId = await PostRunAsync(segmentId, admin);
                await Eventually.GetAsync(
                    async () => (await FindRunAsync(segmentId, runId, admin)) is { Status: "Failed" } run ? run : null,
                    $"failing run {i} finishes failing");
                failedRunIds.Add(runId);
            }
        }

        var remaining = await SegmentsDb.ListRunIdsAsync(stack.SqlConnectionString, tenant.TenantId, segmentId);
        remaining.Should().HaveCount(4, "the newest three failures plus the current run, which is not among them");
        remaining.Should().BeEquivalentTo(failedRunIds.TakeLast(3).Append(currentRunId));

        var state = await SegmentsDb.GetSegmentStateAsync(stack.SqlConnectionString, tenant.TenantId, segmentId);
        state.CurrentRunId.Should().Be(currentRunId);
        (await SegmentsDb.CountRunMembersAsync(stack.SqlConnectionString, currentRunId)).Should().Be(412, "the current run's members survive its own pruning immunity");
    }

    private async Task<(SeededTenant Tenant, string Admin)> SeededTenantAsync()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        _tenantIds.Add(tenant.TenantId);
        await SeedDataset.LoadAsync(_factory, tenant.TenantId);
        var admin = TenancySeed.AccessToken(_factory, tenant.AdminUserId, tenant.TenantId, Roles.Admin);
        return (tenant, admin);
    }

    private async Task<Guid> CreateSegmentAsync(string bearer, string name, string fixtureFileName)
    {
        var definition = RuleFixtures.LoadDefinition(fixtureFileName);
        using var response = await _client.PostAsync("/v1/segments", bearer, new { name, definition });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone().GetProperty("id").GetGuid();
    }

    private async Task<Guid> PostRunAsync(Guid segmentId, string bearer)
    {
        using var response = await _client.PostAsync($"/v1/segments/{segmentId}/run", bearer, new { });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<RunQueuedResponse>();
        return body!.RunId;
    }

    private async Task<SegmentRunListItem?> FindRunAsync(Guid segmentId, Guid runId, string bearer)
    {
        var page = await _client.ReadAsync<SegmentRunListResponse>($"/v1/segments/{segmentId}/runs?limit=200", bearer);
        return page.Items.SingleOrDefault(i => i.RunId == runId);
    }

    private async Task<SegmentRunListItem> WaitForFinishedAsync(Guid segmentId, Guid runId, string bearer)
        => await Eventually.GetAsync(
            async () => (await FindRunAsync(segmentId, runId, bearer)) is { Status: "Finished" } run ? run : null,
            $"run {runId} finishes");

    /// <summary>Pages GET /segments/{id}/members to completion (task-8 brief) — every id currently reachable
    /// through the HTTP read, not just one page's worth.</summary>
    private async Task<IReadOnlyList<Guid>> AllMemberIdsAsync(Guid segmentId, string bearer)
    {
        var ids = new List<Guid>();
        string? cursor = null;
        do
        {
            var path = cursor is null
                ? $"/v1/segments/{segmentId}/members?limit=200"
                : $"/v1/segments/{segmentId}/members?limit=200&cursor={Uri.EscapeDataString(cursor)}";
            var page = await _client.ReadAsync<SegmentMemberListResponse>(path, bearer);
            ids.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return ids;
    }

    private async Task<IReadOnlyList<Guid>> TopRunMemberIdsAsync(Guid runId, int count)
    {
        await using var connection = new SqlConnection(stack.SqlConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TOP (@count) ProfileId FROM segments.SegmentRunMembers WHERE RunId = @run ORDER BY ProfileId";
        command.Parameters.Add(new SqlParameter("@count", count));
        command.Parameters.Add(new SqlParameter("@run", runId));
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>Profiles not currently a member of <paramref name="currentRunId"/> that already satisfy the
    /// non-`plan` half of `seed-pro-id` (country in ID/SG, or no `app_open` in the last 7 days) — IT-017 sets
    /// `plan = pro` on these so they join on the next run.</summary>
    private async Task<IReadOnlyList<Guid>> FindOtherwiseQualifyingNonMembersAsync(Guid tenantId, Guid currentRunId, int count)
    {
        var since = DateTime.UtcNow.AddDays(-7);
        await using var connection = new SqlConnection(stack.SqlConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT TOP (@count) p.Id FROM profiles.Profiles p
            WHERE p.TenantId = @tenant AND p.MergedIntoId IS NULL
              AND p.Id NOT IN (SELECT ProfileId FROM segments.SegmentRunMembers WHERE RunId = @run)
              AND (p.Attr_plan IS NULL OR p.Attr_plan <> 'pro')
              AND (p.Attr_country IN ('ID', 'SG') OR NOT EXISTS (
                    SELECT 1 FROM profiles.Events e
                    WHERE e.TenantId = @tenant AND e.ProfileId = p.Id AND e.Name = 'app_open' AND e.OccurredAt >= @since))
            ORDER BY p.Id
            """;
        command.Parameters.Add(new SqlParameter("@count", count));
        command.Parameters.Add(new SqlParameter("@tenant", tenantId));
        command.Parameters.Add(new SqlParameter("@run", currentRunId));
        command.Parameters.Add(new SqlParameter("@since", SqlDbType.DateTime2) { Value = since });
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        ids.Should().HaveCount(count, "the seed dataset is large enough to offer this many otherwise-qualifying non-members");
        return ids;
    }

}
