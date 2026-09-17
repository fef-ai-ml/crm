using System.Collections.Concurrent;

namespace Crm.Tenancy.Infrastructure.Auth;

/// <summary>In-memory, per-process throttle: LastUsedAt is written at most once per minute per key prefix.</summary>
public sealed class ApiKeyUsageTracker
{
    private static readonly long Interval = TimeSpan.FromMinutes(1).Ticks;
    private readonly ConcurrentDictionary<string, long> _lastTouch = new(StringComparer.Ordinal);

    public bool ShouldTouch(string prefix, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        var now = utcNow.Ticks;
        while (true)
        {
            if (!_lastTouch.TryGetValue(prefix, out var last))
            {
                if (_lastTouch.TryAdd(prefix, now))
                {
                    return true;
                }

                continue;
            }

            if (now - last < Interval)
            {
                return false;
            }

            if (_lastTouch.TryUpdate(prefix, now, last))
            {
                return true;
            }
        }
    }
}
