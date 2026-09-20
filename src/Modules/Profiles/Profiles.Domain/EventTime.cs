namespace Crm.Profiles.Domain;

/// <summary>
/// profiles.Events.OccurredAt is datetime2(3) and half of the idempotency key (Id, OccurredAt): the value is
/// truncated to milliseconds and clamped to ReceivedAt +/- 7 d before anything reads it, so a redelivered message
/// computes exactly the value that is already stored.
/// </summary>
public static class EventTime
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(7);

    public static DateTime Normalize(DateTime occurredAt, DateTime receivedAt)
    {
        var received = TruncateToMilliseconds(receivedAt);
        var occurred = TruncateToMilliseconds(occurredAt);
        if (occurred < received - Window)
        {
            return received - Window;
        }

        return occurred > received + Window ? received + Window : occurred;
    }

    public static DateTime TruncateToMilliseconds(DateTime value)
        => new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
}
