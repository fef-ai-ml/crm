using Crm.Kernel.Time;
using Crm.Profiles.Infrastructure.Consumers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Crm.Profiles.Infrastructure.Maintenance;

/// <summary>
/// docs/data-dictionary/profiles.md: "a maintenance job adds the next month's boundary". OccurredAt is clamped to
/// ReceivedAt ± 7 d, so rows only ever land in the previous, current or next month; keeping two months ahead means a
/// SPLIT always cuts an empty range and is a metadata operation.
/// </summary>
internal sealed class EventPartitionMaintenanceService(IServiceScopeFactory scopes, SchemaGate schemaGate, IClock clock, ILogger<EventPartitionMaintenanceService> logger) : BackgroundService
{
    private const int MonthsAhead = 2;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    internal async Task<int> EnsureAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProfilesDbContext>();
        var added = 0;
        foreach (var boundary in PartitionBoundaries.Upcoming(utcNow, MonthsAhead))
        {
            added += await EnsureBoundaryAsync(db, boundary, cancellationToken);
        }

        return added;
    }

    /// <summary>
    /// One batch per boundary: check and split cannot interleave with another Worker thanks to the applock, which is
    /// scoped to the transaction so it releases as soon as the batch commits. <c>Database.SqlQuery</c> sends the
    /// interpolated text verbatim when nothing composes over it, so the multi-statement batch runs as one round trip.
    /// </summary>
    private static async Task<int> EnsureBoundaryAsync(ProfilesDbContext db, DateTime boundary, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQuery<int>(
            $"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            EXEC sp_getapplock @Resource = N'PF_Events_Monthly', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 30000;
            DECLARE @added int = 0;
            IF NOT EXISTS (
                SELECT 1 FROM sys.partition_range_values v
                JOIN sys.partition_functions f ON f.function_id = v.function_id
                WHERE f.name = N'PF_Events_Monthly' AND CAST(v.value AS datetime2(3)) = {boundary})
            BEGIN
                ALTER PARTITION SCHEME PS_Events_Monthly NEXT USED [PRIMARY];
                ALTER PARTITION FUNCTION PF_Events_Monthly() SPLIT RANGE ({boundary});
                SET @added = 1;
            END
            COMMIT TRANSACTION;
            SELECT @added AS [Value];
            """).ToListAsync(cancellationToken);
        return rows.Single();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await schemaGate.WaitAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var added = await EnsureAsync(clock.UtcNow, stoppingToken);
                    if (added > 0 && logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation("Added {Count} monthly boundary(ies) to PF_Events_Monthly", added);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Not urgent: rows beyond the last boundary still land in the last partition.
                    logger.LogError(ex, "Partition maintenance failed; next attempt in {Interval}", Interval);
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }
}
