namespace Crm.Kernel.Ids;

public interface IIdGenerator
{
    Guid NewId();

    /// <summary>UUIDv7 whose time component is <paramref name="utcTimestamp"/> rather than the system clock, so callers that own an IClock get ids that sort by their own notion of time.</summary>
    Guid NewId(DateTime utcTimestamp);
}

public sealed class UuidV7IdGenerator : IIdGenerator
{
    public Guid NewId() => Guid.CreateVersion7();

    public Guid NewId(DateTime utcTimestamp)
        => Guid.CreateVersion7(new DateTimeOffset(DateTime.SpecifyKind(utcTimestamp, DateTimeKind.Utc)));
}
