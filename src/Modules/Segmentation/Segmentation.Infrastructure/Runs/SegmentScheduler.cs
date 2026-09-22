using Crm.Kernel.Tenancy;
using Crm.Segmentation.Application.Runs;
using Crm.Segmentation.Domain.Segments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crm.Segmentation.Infrastructure.Runs;

/// <summary>
/// H3: every <see cref="SegmentScheduleOptions.Interval"/> (default six hours), queues a Schedule-trigger run for
/// every Active segment (all tenants) that has no run already Queued or Running. <see cref="EnqueueScheduledRunsAsync"/>
/// is public so a test can call it directly instead of waiting on the timer (IT-021).
/// </summary>
internal sealed class SegmentScheduler(
    IServiceScopeFactory scopes,
    IOptions<SegmentScheduleOptions> options,
    ILogger<SegmentScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // never delay host start-up
        await SegmentSchemaGate.WaitAsync(scopes, logger, stoppingToken);

        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await EnqueueScheduledRunsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduled segment run enqueue failed; retrying on the next interval");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task EnqueueScheduledRunsAsync(CancellationToken cancellationToken)
    {
        List<Candidate> candidates;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SegmentationDbContext>();

            // Cross-tenant read (IgnoreQueryFilters): the scheduler has no tenant of its own.
            candidates = await db.Segments.IgnoreQueryFilters()
                .Where(s => s.Status == SegmentStatus.Active)
                .Where(s => !db.SegmentRuns.IgnoreQueryFilters()
                    .Any(r => r.SegmentId == s.Id && (r.Status == SegmentRunStatus.Queued || r.Status == SegmentRunStatus.Running)))
                .Select(s => new Candidate(s.TenantId, s.Id))
                .ToListAsync(cancellationToken);
        }

        foreach (var candidate in candidates)
        {
            // B6 (parked pair, one fix): one candidate's exception must never abort the sweep for the rest —
            // the review found this loop turning a reap-related InvalidOperationException (I-2) into "the
            // remaining segments do not get scheduled this tick" for every segment after the failing one.
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(candidate.TenantId, null, TenantPrincipalKind.None);

                var queue = scope.ServiceProvider.GetRequiredService<ISegmentRunQueue>();
                var result = await queue.EnqueueAsync(candidate.SegmentId, SegmentRunTrigger.Schedule, cancellationToken);

                // Lost a race against a Manual run queued between the candidate read above and here, or the
                // segment was archived meanwhile; neither is worth failing the whole sweep over.
                if (result.IsFailure && logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Scheduled run for segment {SegmentId} was not queued: {Code}", candidate.SegmentId, result.Error!.Code);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduled run enqueue for segment {SegmentId} threw; continuing the sweep for the remaining candidates", candidate.SegmentId);
            }
        }
    }

    private sealed record Candidate(Guid TenantId, Guid SegmentId);
}

/// <summary>Segmentation:Schedule configuration (H3): how often <see cref="SegmentScheduler"/> enqueues
/// Schedule-trigger runs for Active segments.</summary>
public sealed class SegmentScheduleOptions
{
    public const string SectionName = "Segmentation:Schedule";
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(6);

    public TimeSpan Interval { get; set; } = DefaultInterval;
}
