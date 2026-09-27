using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using Crm.Contracts;
using Crm.Segmentation.Infrastructure.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crm.Segmentation.Infrastructure.Realtime;

/// <summary>
/// `events.processed` → real-time segment membership (spec §5.4, ruling I8). <see cref="RealtimeOptions.Instances"/>
/// consumers in group `segmentation`, each processing its partitions sequentially — one profile's messages share a
/// key, so they are evaluated in order — with a synchronous offset commit per record. The handler never fails a
/// record (I9); anything that fails outside it tears the consumer down, and it is rebuilt after a pause, Kafka
/// redelivering from the last committed offset (the same shape as Profiles' raw-events consumer, ruling E9).
/// </summary>
internal sealed class ProcessedEventsConsumerService(
    IOptions<RealtimeOptions> options,
    IServiceScopeFactory scopes,
    ILogger<ProcessedEventsConsumerService> logger) : BackgroundService
{
    private static readonly ActivitySource Source = new("Crm.Segmentation");
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // never delay host start-up

        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("events.processed consumer is disabled (Segmentation:Realtime:Enabled=false)");
            return;
        }

        try
        {
            await SegmentSchemaGate.WaitAsync(scopes, logger, stoppingToken);
            var instances = Enumerable.Range(0, Math.Max(1, settings.Instances))
                .Select(instance => Task.Run(() => RunInstanceAsync(instance, settings, stoppingToken), stoppingToken));
            await Task.WhenAll(instances);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    private async Task RunInstanceAsync(int instance, RealtimeOptions settings, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var consumer = BuildConsumer(instance, settings);
                consumer.Subscribe(KafkaTopics.EventsProcessed);
                try
                {
                    await ConsumeLoopAsync(consumer, stoppingToken);
                }
                finally
                {
                    CloseQuietly(consumer);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "events.processed consumer {Instance} stopped outside message processing; rebuilding in {DelayMs} ms, uncommitted records will be redelivered", instance, settings.RestartDelayMs);
                try
                {
                    await Task.Delay(settings.RestartDelayMs, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ConsumeLoopAsync(IConsumer<string, byte[]> consumer, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = consumer.Consume(PollInterval);
            if (result?.Message is null)
            {
                continue;
            }

            var envelope = ToEnvelope(result);
            using (var activity = StartActivity(envelope))
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<RealtimeMessageHandler>().HandleAsync(envelope, stoppingToken);
            }

            consumer.Commit(result);
        }

        stoppingToken.ThrowIfCancellationRequested();
    }

    private static IConsumer<string, byte[]> BuildConsumer(int instance, RealtimeOptions settings)
        => new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = settings.BootstrapServers,
            GroupId = settings.GroupId,
            ClientId = $"crm-worker-segmentation-{instance}",
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnablePartitionEof = false,
        }).Build();

    private void CloseQuietly(IConsumer<string, byte[]> consumer)
    {
        try
        {
            consumer.Close(); // leave the group now instead of after the session timeout
        }
        catch (KafkaException ex)
        {
            logger.LogDebug(ex, "Closing the events.processed consumer failed");
        }
    }

    private static ProcessedEnvelope ToEnvelope(ConsumeResult<string, byte[]> result)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in result.Message.Headers)
        {
            headers[header.Key] = Encoding.UTF8.GetString(header.GetValueBytes() ?? []);
        }

        return new ProcessedEnvelope(result.Topic, result.Partition.Value, result.Offset.Value, result.Message.Key, result.Message.Value ?? [], headers);
    }

    private static Activity? StartActivity(ProcessedEnvelope envelope)
    {
        var name = $"{envelope.Topic} process";
        var activity = envelope.Headers.TryGetValue(KafkaHeaders.TraceParent, out var traceParent) && ActivityContext.TryParse(traceParent, null, out var parent)
            ? Source.StartActivity(name, ActivityKind.Consumer, parent)
            : Source.StartActivity(name, ActivityKind.Consumer);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", envelope.Topic);
        activity?.SetTag("messaging.kafka.partition", envelope.Partition);
        activity?.SetTag("messaging.kafka.offset", envelope.Offset);
        if (envelope.Headers.TryGetValue(KafkaHeaders.TenantId, out var tenantId))
        {
            activity?.SetTag("crm.tenant_id", tenantId);
        }

        return activity;
    }
}
