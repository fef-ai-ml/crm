namespace Crm.Ingestion.Infrastructure.Kafka;

public sealed class KafkaProducerOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = string.Empty;

    public ProducerSettings Producer { get; set; } = new();

    public sealed class ProducerSettings
    {
        /// <summary>How long a record may wait for the broker's acknowledgement before the request fails with 503.</summary>
        public int MessageTimeoutMs { get; set; } = 3000;

        public int LingerMs { get; set; } = 5;
    }
}
