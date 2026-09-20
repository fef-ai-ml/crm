namespace Crm.Profiles.Domain;

/// <summary>A MergedIntoId chain loops, dangles or is implausibly long. Data corruption: the message is dead-lettered.</summary>
public sealed class MergeChainCorruptException : Exception
{
    public MergeChainCorruptException()
    {
    }

    public MergeChainCorruptException(string message)
        : base(message)
    {
    }

    public MergeChainCorruptException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
