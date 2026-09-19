using Crm.Kernel.Time;

namespace Crm.Ingestion.UnitTests;

internal sealed class FakeClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; set; } = utcNow;
}
