using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using Crm.Contracts;
using Crm.Profiles.Application.Messages;
using Crm.Profiles.Application.Pipeline;
using Crm.Profiles.Application.Retry;
using Crm.Profiles.Infrastructure.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crm.Profiles.Infrastructure.Consumers;

/// <summary>
/// events.raw → profiles. Order per record (spec §4.2 step 5, NFR-04): SQL commit → publish to events.processed →
/// commit the offset. A record that fails every attempt goes to events.dlq and its offset is committed (FR-07).
/// Anything that fails outside message processing tears the consumer down; it is rebuilt after a pause and Kafka
/// redelivers from the last committed offset (ruling E9).
/// </summary>
internal sealed class RawEventsConsumerService(
    IOptions<ProfilesConsumerOptions> options,
    IRawMessageHandler handler,
    IProcessedPublisher processed,
    IDeadLetterPublisher deadLetters,
    IPipelineHooks hooks,
    SchemaGate schemaGate,
    ProfilesMetrics metrics,
    ILogger<RawEventsConsumerService> logger) : BackgroundService
{
    private static readonly ActivitySource Source = new("Crm.Profiles");
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LagInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // never delay host start-up (see StartupProbeService)

        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("events.raw consumer is disabled (Profiles:Consumer:Enabled=false)");
            return;
        }

        try
        {
            await schemaGate.WaitAsync(stoppingToken);
            var instances = Enumerable.Range(0, Math.Max(1, settings.Instances))
                .Select(instance => Task.Run(() => RunInstanceAsync(instance, settings, stoppingToken), stoppingToken));
            await Task.WhenAll(instances);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    private async Task RunInstanceAsync(int instance, ProfilesConsumerOptions settings, CancellationToken stoppingToken)
    {
        var retry = new MessageRetryPolicy(settings.RetryDelays);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var consumer = BuildConsumer(instance, settings);
                consumer.Subscribe(KafkaTopics.EventsRaw);
                try
                {
                    await ConsumeLoopAsync(consumer, retry, stoppingToken);
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
                logger.LogError(ex, "events.raw consumer {Instance} stopped outside message processing; rebuilding in {DelayMs} ms, uncommitted records will be redelivered", instance, settings.RestartDelayMs);
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

    private async Task ConsumeLoopAsync(IConsumer<string, byte[]> consumer, MessageRetryPolicy retry, CancellationToken stoppingToken)
    {
        var sinceLagReport = Stopwatch.StartNew();
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = consumer.Consume(PollInterval);
            if (sinceLagReport.Elapsed >= LagInterval)
            {
                ReportLag(consumer);
                sinceLagReport.Restart();
            }

            if (result?.Message is null)
            {
                continue;
            }

            await ProcessAsync(result, retry, stoppingToken);
            consumer.Commit(result);
        }

        stoppingToken.ThrowIfCancellationRequested();
    }

    private async Task ProcessAsync(ConsumeResult<string, byte[]> result, MessageRetryPolicy retry, CancellationToken stoppingToken)
    {
        var envelope = ToEnvelope(result);
        using var activity = StartActivity(envelope);

        var attempt = await retry.ExecuteAsync((_, token) => handler.HandleAsync(envelope, token), stoppingToken);
        if (!attempt.Succeeded)
        {
            activity?.SetStatus(ActivityStatusCode.Error, attempt.Failure!.Message);
            logger.LogError(attempt.Failure, "Dead-lettering {Topic}[{Partition}]@{Offset} after {Attempts} attempts", envelope.Topic, envelope.Partition, envelope.Offset, attempt.Attempts);
            await deadLetters.PublishAsync(envelope, attempt.Failure!, attempt.Attempts, stoppingToken);
            metrics.DeadLettered(envelope.Topic);
            return;
        }

        await hooks.SqlCommittedAsync(envelope, stoppingToken);
        foreach (var message in attempt.Value!.Messages)
        {
            await processed.PublishAsync(message, stoppingToken);
        }
    }

    private IConsumer<string, byte[]> BuildConsumer(int instance, ProfilesConsumerOptions settings)
        => new ConsumerBuilder<string, byte[]>(new ConsumerConfig
            {
                BootstrapServers = settings.BootstrapServers,
                GroupId = settings.GroupId,
                ClientId = $"crm-worker-profiles-{instance}",
                EnableAutoCommit = false,
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnablePartitionEof = false,
            })
            .SetPartitionsRevokedHandler((_, partitions) => ClearLag(partitions.Select(p => p.TopicPartition)))
            .SetPartitionsLostHandler((_, partitions) => ClearLag(partitions.Select(p => p.TopicPartition)))
            .Build();

    private void ReportLag(IConsumer<string, byte[]> consumer)
    {
        foreach (var partition in consumer.Assignment)
        {
            var high = consumer.GetWatermarkOffsets(partition).High; // cached from the last fetch; no network call
            var position = consumer.Position(partition);
            if (high == Offset.Unset || position == Offset.Unset)
            {
                continue; // nothing fetched from this partition yet
            }

            metrics.SetLag(partition.Topic, partition.Partition.Value, Math.Max(0, high.Value - position.Value));
        }
    }

    private void ClearLag(IEnumerable<TopicPartition> partitions)
    {
        foreach (var partition in partitions)
        {
            metrics.ClearLag(partition.Topic, partition.Partition.Value);
        }
    }

    private void CloseQuietly(IConsumer<string, byte[]> consumer)
    {
        try
        {
            consumer.Close(); // leave the group now instead of after the session timeout
        }
        catch (KafkaException ex)
        {
            logger.LogDebug(ex, "Closing the events.raw consumer failed");
        }
    }

    private static RawEnvelope ToEnvelope(ConsumeResult<string, byte[]> result)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in result.Message.Headers)
        {
            headers[header.Key] = Encoding.UTF8.GetString(header.GetValueBytes() ?? []);
        }

        return new RawEnvelope(result.Topic, result.Partition.Value, result.Offset.Value, result.Message.Key, result.Message.Value ?? [], headers);
    }

    private static Activity? StartActivity(RawEnvelope envelope)
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
