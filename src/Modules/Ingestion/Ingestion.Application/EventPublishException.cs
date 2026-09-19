namespace Crm.Ingestion.Application;

public sealed class EventPublishException : Exception
{
    public EventPublishException()
    {
    }

    public EventPublishException(string message)
        : base(message)
    {
    }

    public EventPublishException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
