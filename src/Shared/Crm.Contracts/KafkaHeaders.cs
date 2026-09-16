namespace Crm.Contracts;

public static class KafkaHeaders
{
    public const string TenantId = "tenant-id";
    public const string MessageType = "message-type";
    public const string ContentType = "content-type";
    public const string TraceParent = "traceparent";

    public const string DlqError = "error";
    public const string DlqStack = "stack";
    public const string DlqAttempt = "attempt";
    public const string DlqSourceTopic = "source-topic";
    public const string DlqSourcePartition = "source-partition";
    public const string DlqSourceOffset = "source-offset";

    public const string JsonContentType = "application/json";
}
