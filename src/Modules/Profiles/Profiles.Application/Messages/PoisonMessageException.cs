namespace Crm.Profiles.Application.Messages;

/// <summary>The record can never be processed: wrong shape, wrong tenant, unknown type.</summary>
public sealed class PoisonMessageException : Exception
{
    public PoisonMessageException()
    {
    }

    public PoisonMessageException(string message)
        : base(message)
    {
    }

    public PoisonMessageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
