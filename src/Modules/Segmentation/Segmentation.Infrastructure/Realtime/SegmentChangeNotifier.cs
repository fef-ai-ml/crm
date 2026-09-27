using Crm.Kernel.Redis;
using Crm.Segmentation.Domain.Segments;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Crm.Segmentation.Infrastructure.Realtime;

/// <summary>
/// The API's side of `segments:changed` (rulings I7, I13): after a segment is saved or archived, a set nothing
/// maintains any more is deleted, then the tenant id (32 hex characters) is published so every Worker drops its
/// cached segment list. Both steps are best-effort — Redis is rebuildable state (ADR-0006) and the Worker's cache
/// TTL covers a lost message — so neither ever fails the save.
/// </summary>
internal sealed class SegmentChangeNotifier(IConnectionMultiplexer redis, RealtimeMembershipStore store, ILogger<SegmentChangeNotifier> logger)
{
    public static readonly RedisChannel Channel = RedisChannel.Literal(RedisKeys.SegmentsChangedChannel);

    public async Task SegmentSavedAsync(Segment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        try
        {
            if (!IsMaintained(segment))
            {
                await store.DeleteMembersAsync(segment.TenantId, segment.Id);
            }

            await redis.GetSubscriber().PublishAsync(Channel, segment.TenantId.ToString("N"));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "segments:changed for segment {SegmentId} was not published; Workers pick the change up when their cache expires", segment.Id);
        }
    }

    /// <summary>I12/I13: only an Active, eligible segment has a real-time set someone keeps current.</summary>
    public static bool IsMaintained(Segment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return segment.Status == SegmentStatus.Active && segment.IsRealtimeEligible;
    }
}

/// <summary>The Worker's side of `segments:changed` (I6): drops the tenant's cached segment list. The multiplexer
/// re-subscribes by itself after a reconnect; a subscribe that fails at start-up is retried.</summary>
internal sealed class SegmentChangeSubscriber(IConnectionMultiplexer redis, RealtimeSegmentCache cache, ILogger<SegmentChangeSubscriber> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // never delay host start-up

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await redis.GetSubscriber().SubscribeAsync(SegmentChangeNotifier.Channel, (_, message) => OnMessage(message));
                return;
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                logger.LogWarning(ex, "Subscribing to segments:changed failed; retrying in {Delay}", RetryDelay);
                try
                {
                    await Task.Delay(RetryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private void OnMessage(RedisValue message)
    {
        if (Guid.TryParseExact((string?)message, "N", out var tenantId))
        {
            cache.Invalidate(tenantId);
        }
        else
        {
            logger.LogWarning("Ignoring a segments:changed message that is not a tenant id");
        }
    }
}
