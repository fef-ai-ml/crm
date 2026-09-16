using Crm.Kernel.Redis;
using FluentAssertions;
using Xunit;

namespace Crm.Kernel.UnitTests;

[Trait("Category", "Unit")]
public class RedisKeysTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Segment = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid Profile = Guid.Parse("99999999-8888-7777-6666-555555555555");

    [Fact]
    public void Identity_cache_key_shape()
        => RedisKeys.IdentityCache(Tenant, "email", "a@b.com")
            .Should().Be("t:11111111222233334444555555555555:id:email:a@b.com");

    [Fact]
    public void Segment_members_key_shape()
        => RedisKeys.SegmentMembers(Tenant, Segment)
            .Should().Be("t:11111111222233334444555555555555:seg:aaaaaaaabbbbccccddddeeeeeeeeeeee:members");

    [Fact]
    public void Event_counter_key_includes_event_name_and_window()
        => RedisKeys.EventCounter(Tenant, Segment, "purchase", "30d", Profile)
            .Should().Be("t:11111111222233334444555555555555:seg:aaaaaaaabbbbccccddddeeeeeeeeeeee:ev:purchase:30d:99999999888877776666555555555555");

    [Fact]
    public void Preview_key_shape()
        => RedisKeys.PreviewCount(Tenant, "abc123").Should().Be("t:11111111222233334444555555555555:preview:abc123");

    [Fact]
    public void Api_key_cache_and_channel_are_the_only_unprefixed_keys()
    {
        RedisKeys.ApiKey("AbCdEfGh").Should().Be("apikey:AbCdEfGh");
        RedisKeys.SegmentsChangedChannel.Should().Be("segments:changed");
    }

    [Fact]
    public void Every_tenant_scoped_builder_starts_with_tenant_prefix()
    {
        var prefix = "t:11111111222233334444555555555555:";
        new[]
        {
            RedisKeys.IdentityCache(Tenant, "phone", "+62"),
            RedisKeys.SegmentMembers(Tenant, Segment),
            RedisKeys.EventCounter(Tenant, Segment, "x", "7d", Profile),
            RedisKeys.PreviewCount(Tenant, "h"),
        }.Should().OnlyContain(k => k.StartsWith(prefix, StringComparison.Ordinal));
    }
}
