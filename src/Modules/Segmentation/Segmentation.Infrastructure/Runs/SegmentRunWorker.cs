using System.Diagnostics;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Segmentation.Application.Runs;
using Crm.Segmentation.Domain.Segments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crm.Segmentation.Infrastructure.Runs;

/// <summary>
/// Drains queued batch runs sequentially every <see cref="SegmentRunOptions.PollInterval"/> (default 5 s; H3: no
/// Quartz.NET). Each run gets its own DI scope with the tenant context resolved to the run's own tenant, exactly
/// as Profiles.Infrastructure.Consumers.ScopedMessageHandler does for a consumed message. An exception in one run
/// is logged and the run marked Failed; the loop always continues to the next queued run.
/// </summary>
internal sealed class SegmentRunWorker(
    IServiceScopeFactory scopes,
    SegmentationMetrics metrics,
    IOptions<SegmentRunOptions> options,
    ILogger<SegmentRunWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // never delay host start-up
        await SegmentSchemaGate.WaitAsync(scopes, logger, stoppingToken);

        using var timer = new PeriodicTimer(options.Value.PollInterval);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Segment run worker tick failed; retrying on the next poll");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync(CancellationToken stoppingToken)
    {
        await ReapAbandonedRunsAsync(stoppingToken);

        foreach (var queued in await ReadQueuedAsync(stoppingToken))
        {
            await RunOneAsync(queued, stoppingToken);
        }
    }

    /// <summary>H4/H29: fails any run stuck `Running` past the reap window
    /// (<see cref="SegmentationOptionsDefaults.ReapCutoff"/>, derived from the configured command timeout budget
    /// — see B2/I-2's finding that a flat 10-minute window can be shorter than a large run's legitimate
    /// wall-clock ceiling). Public, mirroring <c>SegmentScheduler.EnqueueScheduledRunsAsync</c>'s exposure (fix
    /// round 1, item 4), so a test can invoke the reap directly instead of waiting on the poll and the clock.</summary>
    public async Task ReapAbandonedRunsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SegmentationDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var now = clock.UtcNow;
        var abandonedAfter = SegmentationOptionsDefaults.ReapCutoff(options.Value.CommandTimeoutSeconds);
        var cutoff = now - abandonedAfter;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE segments.SegmentRuns SET Status = 'Failed', Error = 'abandoned', FinishedAt = {now}, UpdatedAt = {now} WHERE Status = 'Running' AND StartedAt < {cutoff}",
            cancellationToken);
    }

    /// <summary>Cross-tenant read (IgnoreQueryFilters): the worker has no tenant of its own between runs.</summary>
    private async Task<IReadOnlyList<QueuedRun>> ReadQueuedAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SegmentationDbContext>();
        return await db.SegmentRuns.IgnoreQueryFilters()
            .Where(r => r.Status == SegmentRunStatus.Queued)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .Select(r => new QueuedRun(r.TenantId, r.Id, r.Trigger))
            .ToListAsync(cancellationToken);
    }

    private async Task RunOneAsync(QueuedRun queued, CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(queued.TenantId, null, TenantPrincipalKind.None);
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["TenantId"] = queued.TenantId, ["RunId"] = queued.RunId });

        var db = scope.ServiceProvider.GetRequiredService<SegmentationDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        // H4: atomic claim. Zero rows affected means another instance claimed it, or the reap above just failed
        // it; either way there is nothing left for this instance to run.
        var claimedAt = clock.UtcNow;
        var claimed = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE segments.SegmentRuns SET Status = 'Running', StartedAt = {claimedAt}, UpdatedAt = {claimedAt} WHERE RunId = {queued.RunId} AND Status = 'Queued'",
            stoppingToken);
        if (claimed != 1)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var outcome = "failed";
        try
        {
            var runner = scope.ServiceProvider.GetRequiredService<ISegmentRunner>();
            outcome = await runner.RunAsync(queued.RunId, stoppingToken) ? "finished" : "failed";
        }
        catch (Exception ex)
        {
            // ISegmentRunner.RunAsync is documented never to throw for a run failure; this is a defensive net so
            // an unexpected exception here still never stops the drain loop.
            logger.LogError(ex, "Run {RunId} failed outside the runner's own error handling", queued.RunId);
        }

        metrics.RecordRunDuration(stopwatch.Elapsed.TotalSeconds, queued.Trigger, outcome);
    }

    private sealed record QueuedRun(Guid TenantId, Guid RunId, SegmentRunTrigger Trigger);
}
