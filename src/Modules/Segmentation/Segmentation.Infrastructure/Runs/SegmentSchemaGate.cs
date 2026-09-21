using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Crm.Segmentation.Infrastructure.Runs;

/// <summary>
/// Ruling E11 (mirrors Profiles.Infrastructure.Consumers.SchemaGate): in compose the API applies migrations and
/// the Worker may start first. Both <c>SegmentRunWorker</c> and <c>SegmentScheduler</c> wait here before touching
/// `segments.SegmentRuns`.
/// </summary>
internal static class SegmentSchemaGate
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public static async Task WaitAsync(IServiceScopeFactory scopes, ILogger logger, CancellationToken cancellationToken)
    {
        var announced = false;
        while (true)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<SegmentationDbContext>();
                var present = await db.Database
                    .SqlQuery<int>($"SELECT CASE WHEN OBJECT_ID(N'segments.SegmentRuns') IS NULL THEN 0 ELSE 1 END AS [Value]")
                    .SingleAsync(cancellationToken);
                if (present == 1)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Schema probe failed; the database may not be up yet");
            }

            if (!announced)
            {
                logger.LogInformation("Waiting for schema 'segments' (applied by the API in Development, or by `make migrate`)");
                announced = true;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }
}
