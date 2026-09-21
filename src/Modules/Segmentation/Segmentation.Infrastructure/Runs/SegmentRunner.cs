using System.Text.Json;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Segmentation.Application;
using Crm.Segmentation.Application.Compilation;
using Crm.Segmentation.Application.Runs;
using Crm.Segmentation.Domain.Rules;
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
            await MarkFailedAsync(runId, "segment not found");
            return false;
        }

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
            await MarkFailedAsync(runId, ex.Message);
            return false;
        }

        var previousRunId = segment.CurrentRunId;
        var now = clock.UtcNow;
        var timeout = TimeSpan.FromSeconds(options.Value.CommandTimeoutSeconds);
        db.Database.SetCommandTimeout((int)timeout.TotalSeconds);

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

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE segments.SegmentRuns SET Status = 'Finished', FinishedAt = {now}, MemberCount = {inserted}, CompiledSql = {auditSql}, UpdatedAt = {now}
                WHERE RunId = {runId}
                """,
                cancellationToken);

            // H8: keep the newest 3 Finished/Failed runs per segment; the just-finished run (RunId = runId, whose
            // Status the statement above already set to Finished) always counts as one of the three, and
            // Segments.CurrentRunId (just set to runId above) is therefore never among the deleted rows.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                DELETE r FROM segments.SegmentRuns r
                WHERE r.TenantId = {tenant.TenantId} AND r.SegmentId = {segment.Id} AND r.Status IN ('Finished','Failed') AND r.RunId <> {runId}
                  AND r.RunId NOT IN (
                    SELECT TOP (3) RunId FROM segments.SegmentRuns
                    WHERE (TenantId = {tenant.TenantId} AND SegmentId = {segment.Id} AND Status IN ('Finished','Failed')) OR RunId = {runId}
                    ORDER BY CreatedAt DESC, RunId DESC)
                """,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            await RollbackQuietlyAsync(transaction);
            await MarkFailedAsync(runId, ex.Message);
            return false;
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
    /// <see cref="CancellationToken.None"/> so a cancelled run's failure is still recorded.</summary>
    private async Task MarkFailedAsync(Guid runId, string message)
    {
        var now = clock.UtcNow;
        var error = message.Length > MaxErrorLength ? message[..MaxErrorLength] : message;
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE segments.SegmentRuns SET Status = 'Failed', FinishedAt = {now}, Error = {error}, UpdatedAt = {now} WHERE RunId = {runId}",
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not record run {RunId} as Failed", runId);
        }
    }
}

/// <summary>Segmentation:Run configuration (H6): the whole run executes as one transaction bounded by this
/// command timeout.</summary>
public sealed class SegmentRunOptions
{
    public const string SectionName = "Segmentation:Run";

    public int CommandTimeoutSeconds { get; set; } = 300;
}
