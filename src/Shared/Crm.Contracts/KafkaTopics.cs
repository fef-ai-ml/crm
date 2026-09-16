namespace Crm.Contracts;

public static class KafkaTopics
{
    public const string EventsRaw = "events.raw";
    public const string EventsProcessed = "events.processed";
    public const string EventsDlq = "events.dlq";

    public const int Partitions = 12;

    public const long RawRetentionMs = 7L * 24 * 60 * 60 * 1000;
    public const long ProcessedRetentionMs = 7L * 24 * 60 * 60 * 1000;
    public const long DlqRetentionMs = 30L * 24 * 60 * 60 * 1000;

    public const string ProfilesConsumerGroup = "profiles";
    public const string SegmentationConsumerGroup = "segmentation";
}
