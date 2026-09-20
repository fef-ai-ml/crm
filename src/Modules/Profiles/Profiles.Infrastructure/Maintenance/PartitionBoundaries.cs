namespace Crm.Profiles.Infrastructure.Maintenance;

internal static class PartitionBoundaries
{
    /// <summary>RANGE RIGHT boundaries: the first instant of the current month and of each of the next <paramref name="monthsAhead"/> months.</summary>
    public static IReadOnlyList<DateTime> Upcoming(DateTime utcNow, int monthsAhead)
    {
        var first = new DateTime(utcNow.Year, utcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, monthsAhead + 1).Select(first.AddMonths).ToList();
    }
}
