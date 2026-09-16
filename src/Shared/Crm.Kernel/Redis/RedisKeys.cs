namespace Crm.Kernel.Redis;

/// <summary>
/// The complete Redis key inventory for slice 1 (docs/data-dictionary/redis-and-kafka.md).
/// Every tenant-scoped key starts with t:{tenantId}:; the two exceptions are ApiKey and SegmentsChangedChannel.
/// </summary>
public static class RedisKeys
{
    public const string SegmentsChangedChannel = "segments:changed";

    public static string IdentityCache(Guid tenantId, string kind, string value)
        => $"t:{Hex(tenantId)}:id:{kind}:{value}";

    public static string SegmentMembers(Guid tenantId, Guid segmentId)
        => $"t:{Hex(tenantId)}:seg:{Hex(segmentId)}:members";

    public static string EventCounter(Guid tenantId, Guid segmentId, string eventName, string window, Guid profileId)
        => $"t:{Hex(tenantId)}:seg:{Hex(segmentId)}:ev:{eventName}:{window}:{Hex(profileId)}";

    public static string PreviewCount(Guid tenantId, string definitionHashHex)
        => $"t:{Hex(tenantId)}:preview:{definitionHashHex}";

    public static string ApiKey(string prefix) => $"apikey:{prefix}";

    private static string Hex(Guid id) => id.ToString("N");
}
