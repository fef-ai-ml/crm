using Crm.Kernel.Redis;
using Crm.Profiles.Domain;
using Crm.Profiles.Infrastructure.Caching;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class RedisIdentityCacheTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly IdentityKey Key = IdentityKey.ExternalId("u1");
    private readonly IDatabase _database = Substitute.For<IDatabase>();
    private readonly RedisIdentityCache _cache;

    public RedisIdentityCacheTests()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(_database);
        _cache = new RedisIdentityCache(multiplexer, NullLogger<RedisIdentityCache>.Instance);
    }

    [Fact]
    public async Task UT057_the_key_is_tenant_prefixed_and_the_value_is_the_profile_id_in_hex()
    {
        var profileId = Guid.CreateVersion7();
        _database.StringGetAsync(RedisKeys.IdentityCache(Tenant, Key.Kind, Key.Value), Arg.Any<CommandFlags>()).Returns(new RedisValue(profileId.ToString("N")));

        (await _cache.GetAsync(Tenant, Key)).Should().Be(profileId);
    }

    [Fact]
    public async Task A_miss_and_an_unparsable_value_are_both_null()
    {
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null, new RedisValue("not-a-guid"));

        (await _cache.GetAsync(Tenant, Key)).Should().BeNull();
        (await _cache.GetAsync(Tenant, Key)).Should().BeNull();
    }

    [Fact]
    public async Task E12_redis_failures_degrade_to_a_miss_and_never_throw()
    {
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "down", null, CommandStatus.Unknown));
        _database.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).ThrowsAsync(new RedisTimeoutException(CommandFlags.None, "slow", CommandStatus.Unknown));

        (await _cache.GetAsync(Tenant, Key)).Should().BeNull();
        await _cache.Invoking(c => c.RemoveAsync(Tenant, [Key])).Should().NotThrowAsync();
        await _cache.Invoking(c => c.SetAsync(Tenant, Key, Guid.CreateVersion7())).Should().NotThrowAsync();
    }
}
