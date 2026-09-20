using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Crm.Profiles.Infrastructure.Consumers;

/// <summary>
/// Ruling E11: in compose the API applies migrations and the Worker may start first. Consuming before profiles.Events
/// exists would dead-letter valid records, so the consumer waits here instead.
/// </summary>
internal sealed class SchemaGate(IServiceScopeFactory scopes, ILogger<SchemaGate> logger)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        var announced = false;
        while (true)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ProfilesDbContext>();
                var present = await db.Database
                    .SqlQuery<int>($"SELECT CASE WHEN OBJECT_ID(N'profiles.Events') IS NULL THEN 0 ELSE 1 END AS [Value]")
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
                logger.LogInformation("Waiting for schema 'profiles' (applied by the API in Development, or by `make migrate`)");
                announced = true;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }
}
