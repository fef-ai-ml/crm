using System.Text.Json;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Segmentation.Application;
using Crm.Segmentation.Application.Compilation;
using Crm.Segmentation.Application.Runs;
using Crm.Segmentation.Domain.Rules;
using Crm.Segmentation.Domain.Segments;
using Crm.Segmentation.Infrastructure.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crm.Segmentation.Infrastructure.Runs;

/// <summary>
/// <see cref="ISegmentRunner"/> (task-7 brief): insert members, diff against the previous run, swap the current
/// run, prune old runs — one transaction bounded by <see cref="SegmentRunOptions.CommandTimeoutSeconds"/> (H6).
/// The caller (<c>SegmentRunWorker</c>) has already claimed the run (H4: Queued → Running) and resolved the
/// tenant scope this instance runs in.
/// </summary>
internal sealed class SegmentRunner(
    SegmentationDbContext db,
    RuleCompiler compiler,
    CompiledQueryExecutor executor,
    ISegmentationHooks hooks,
    ITenantContext tenant,
    IClock clock,
    IOptions<SegmentRunOptions> options,
    ILogger<SegmentRunner> logger) : ISegmentRunner
{
    private const int MaxErrorLength = 4000;

    public async Task<bool> RunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.SegmentRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null)
        {
            logger.LogWarning("Run {RunId} was not found when the runner started", runId);
            return false;
        }

        var segment = await db.Segments.AsNoTracking().SingleOrDefaultAsync(s => s.Id == run.SegmentId, cancellationToken);
        if (segment is null)
        {
            // Segments are archived, never deleted (FK_SegmentRuns_SegmentId is NoAction), so this should not
            // happen; treated as a run failure rather than an unhandled exception if it somehow does.
            await MarkFailedAsync(runId, run.SegmentId, currentRunId: null, "segment not found");
            LogFailure(runId, run.SegmentId, run.Trigger, ex: null, reason: "segment not found");
            return false;
        }

        if (segment.Status == SegmentStatus.Archived)
        {
            // H30 (fix wave B5): the segment was archived after this run was queued/claimed. Archiving does not
            // cancel a run already in flight for it (M-2), so the runner itself must refuse to materialise an
            // archived segment — no transaction, nothing written to segments.Segments or MembershipChanges.
            await MarkFailedAsync(runId, segment.Id, segment.CurrentRunId, "segment archived");
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Run {RunId} for segment {SegmentId} (tenant {TenantId}) skipped: segment archived", runId, segment.Id, tenant.TenantId);
            }

            return false;
        }

        // Read once, before anything can fail: reused as the diff's @previous on success AND, on the failure
        // path below, as the run's own CurrentRunId to protect from Minor 4's after-failure prune (fix round 1,
        // item 5) — a failed run never changes CurrentRunId, so this value stays correct throughout.
        var previousRunId = segment.CurrentRunId;

        CompiledQuery compiled;
        string auditSql;
        try
        {
            using var document = JsonDocument.Parse(segment.Definition);
            var parsed = RuleParser.Parse(document.RootElement);
            compiled = compiler.Compile(parsed.Tree!, tenant.TenantId);
            auditSql = compiled.ToAuditText(); // H20: audited before RewriteQuery ever sees it.
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(runId, segment.Id, previousRunId, ex.Message);
            LogFailure(runId, segment.Id, run.Trigger, ex);
            return false;
        }

        var now = clock.UtcNow;
        var timeout = TimeSpan.FromSeconds(options.Value.CommandTimeoutSeconds);
        db.Database.SetCommandTimeout((int)timeout.TotalSeconds);

        // Fix round 1, Important 3: this statement order is load-bearing, not incidental. The long member
        // insert (potentially the slowest statement here, over the whole matching population) runs first and
        // touches only segments.SegmentRunMembers; segments.Segments — the row every GET /segments/{id} reads —
        // is not written until the UPDATE below, and everything from there to COMMIT is a handful of short,
        // indexed statements. A concurrent reader of segments.Segments is never blocked by the long part of this
        // transaction, and once the short swap begins, the project's reliance on READ_COMMITTED_SNAPSHOT (see
        // `Test_database_has_read_committed_snapshot_on`) means a reader sees the last COMMITTED version of the
        // row rather than blocking on it at all — so it observes either the fully-previous or the fully-new run,
        // never a partial one, regardless of exactly when its own read lands relative to this commit.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var inserted = await executor.InsertMembersAsync(compiled, runId, transaction.GetDbTransaction(), timeout, cancellationToken);

            // Awaited inside the transaction, right after the member insert (task-7 ruling, IT-018): the
            // transaction has not yet touched segments.Segments, so a concurrent GET /segments/{id} is not blocked
            // and still sees the previous run's memberCount and CurrentRunId while this pauses here.
            await hooks.MembersInsertedAsync(runId, cancellationToken);

            // H7: RunId = NULL matches nothing, so when previousRunId is null the "Entered" set is everyone and
            // the "Exited" set is empty — the first run needs no special case.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO segments.MembershipChanges (Id, TenantId, SegmentId, ProfileId, Change, Source, OccurredAt)
                SELECT NEWID(), {tenant.TenantId}, {segment.Id}, d.ProfileId, 'Entered', 'Batch', {now} FROM (
                    SELECT ProfileId FROM segments.SegmentRunMembers WHERE RunId = {runId}
                    EXCEPT SELECT ProfileId FROM segments.SegmentRunMembers WHERE RunId = {previousRunId}) d
                """,
                cancellationToken);

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO segments.MembershipChanges (Id, TenantId, SegmentId, ProfileId, Change, Source, OccurredAt)
                SELECT NEWID(), {tenant.TenantId}, {segment.Id}, d.ProfileId, 'Exited', 'Batch', {now} FROM (
                    SELECT ProfileId FROM segments.SegmentRunMembers WHERE RunId = {previousRunId}
                    EXCEPT SELECT ProfileId FROM segments.SegmentRunMembers WHERE RunId = {runId}) d
                """,
                cancellationToken);

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE segments.Segments SET CurrentRunId = {runId}, MemberCount = {inserted}, LastRunAt = {now}, UpdatedAt = {now}
                WHERE TenantId = {tenant.TenantId} AND Id = {segment.Id}
                """,
                cancellationToken);

            // H29 (fix wave B2/I-2): guarded on Status = 'Running' so a run the reaper declared abandoned while
            // this transaction was open can never resurrect as Finished. Zero rows affected means exactly that
            // happened — throw before the prune, below, so the whole transaction (the member insert, both diffs
            // and the segments.Segments swap above) rolls back with it; nothing of a reaped run survives.
            var finished = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE segments.SegmentRuns SET Status = 'Finished', FinishedAt = {now}, MemberCount = {inserted}, CompiledSql = {auditSql}, UpdatedAt = {now}
                WHERE RunId = {runId} AND Status = 'Running'
                """,
                cancellationToken);
            if (finished == 0)
            {
                throw new SegmentRunReapedException($"Run {runId} was reaped (its status left 'Running') before it could finish; discarding its work.");
            }

            // H8: keep the newest 3 Finished/Failed runs per segment; the just-finished run (RunId = runId, whose
            // Status the statement above already set to Finished) always counts as one of the three, and
            // Segments.CurrentRunId (just set to runId above) is therefore never among the deleted rows.
            await PruneTerminalRunsAsync(segment.Id, runId, currentRunId: runId, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            await RollbackQuietlyAsync(transaction);

            if (ex is SegmentRunReapedException)
            {
                // The reaper already recorded Failed/'abandoned' from outside this transaction; MarkFailedAsync's
                // own Status = 'Running' guard below is therefore a no-op here and never overwrites it.
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning("Run {RunId} for segment {SegmentId} (tenant {TenantId}) was reaped while its transaction was still open; rolled back", runId, segment.Id, tenant.TenantId);
                }
            }
            else
            {
                LogFailure(runId, segment.Id, run.Trigger, ex);
            }

            await MarkFailedAsync(runId, segment.Id, previousRunId, ex.Message);
            return false;
        }
    }

    /// <summary>B3/I-3: a failed run must leave a trace an operator can find without knowing to call
    /// <c>GET /v1/segments/{id}/runs</c> — run id, segment id, tenant id and trigger only, never the rule
    /// definition text or any parameter value (no PII). <paramref name="ex"/> is null for a failure this method
    /// detected itself rather than caught (e.g. "segment not found"), in which case <paramref name="reason"/>
    /// carries the message instead.</summary>
    private void LogFailure(Guid runId, Guid segmentId, SegmentRunTrigger trigger, Exception? ex, string? reason = null)
    {
        if (!logger.IsEnabled(LogLevel.Error))
        {
            return;
        }

        if (ex is not null)
        {
            logger.LogError(ex, "Run {RunId} for segment {SegmentId} (tenant {TenantId}, trigger {Trigger}) failed", runId, segmentId, tenant.TenantId, trigger);
        }
        else
        {
            logger.LogError("Run {RunId} for segment {SegmentId} (tenant {TenantId}, trigger {Trigger}) failed: {Reason}", runId, segmentId, tenant.TenantId, trigger, reason);
        }
    }

    private async Task RollbackQuietlyAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Rollback after a failed run threw; the connection may already be broken");
        }
    }

    /// <summary>H6: recorded outside the rolled-back transaction, on its own implicit transaction, with
    /// <see cref="CancellationToken.None"/> so a cancelled run's failure is still recorded. Fix round 1, item 5
    /// (Minor 4): also prunes, since without this a segment that only ever fails would accumulate Failed rows
    /// forever — <paramref name="currentRunId"/> (the segment's CurrentRunId, unaffected by this failure) is
    /// protected from deletion the same way the just-finished run protects itself on the success path. H29 (fix
    /// wave B2): guarded on <c>Status = 'Running'</c> the same way the finish update is, so this can never
    /// overwrite a status the reaper already set (e.g. 'abandoned') — affecting zero rows here is expected and
    /// not itself logged as an error.</summary>
    private async Task MarkFailedAsync(Guid runId, Guid segmentId, Guid? currentRunId, string message)
    {
        var now = clock.UtcNow;
        var error = message.Length > MaxErrorLength ? message[..MaxErrorLength] : message;
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE segments.SegmentRuns SET Status = 'Failed', FinishedAt = {now}, Error = {error}, UpdatedAt = {now} WHERE RunId = {runId} AND Status = 'Running'",
                CancellationToken.None);

            await PruneTerminalRunsAsync(segmentId, runId, currentRunId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not record run {RunId} as Failed", runId);
        }
    }

    /// <summary>H8: keeps the newest 3 Finished/Failed runs for the segment by CreatedAt DESC, RunId DESC, plus
    /// <paramref name="currentRunId"/> if it is not already among them (fix round 1, item 5 — on the success
    /// path <paramref name="currentRunId"/> is always <paramref name="justRunId"/>, so this reduces to the
    /// original H8 statement; on the failure path they differ, since a failed run never becomes CurrentRunId).
    /// Queued/Running rows are never touched (excluded by the `Status IN (...)` filter on both sides).</summary>
    private async Task PruneTerminalRunsAsync(Guid segmentId, Guid justRunId, Guid? currentRunId, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            DELETE r FROM segments.SegmentRuns r
            WHERE r.TenantId = {tenant.TenantId} AND r.SegmentId = {segmentId} AND r.Status IN ('Finished','Failed')
              AND r.RunId <> {justRunId}
              AND ({currentRunId} IS NULL OR r.RunId <> {currentRunId})
              AND r.RunId NOT IN (
                SELECT TOP (3) RunId FROM segments.SegmentRuns
                WHERE (TenantId = {tenant.TenantId} AND SegmentId = {segmentId} AND Status IN ('Finished','Failed')) OR RunId = {justRunId}
                ORDER BY CreatedAt DESC, RunId DESC)
            """,
            cancellationToken);
    }
}

/// <summary>Segmentation:Run configuration (H6): the whole run executes as one transaction bounded by this
/// command timeout. <see cref="PollInterval"/> is how often <see cref="SegmentRunWorker"/> drains queued runs
/// (default 5 s); tests bind it much shorter so the drain loop does not dominate suite duration.</summary>
public sealed class SegmentRunOptions
{
    public const string SectionName = "Segmentation:Run";
    public const int DefaultCommandTimeoutSeconds = 300;
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    public int CommandTimeoutSeconds { get; set; } = DefaultCommandTimeoutSeconds;

    public TimeSpan PollInterval { get; set; } = DefaultPollInterval;
}
