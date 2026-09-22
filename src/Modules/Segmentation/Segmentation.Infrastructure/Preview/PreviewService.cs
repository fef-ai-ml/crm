using System.Text.Json;
using System.Text.Json.Serialization;
using Crm.Kernel;
using Crm.Kernel.Redis;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Segmentation.Application.Compilation;
using Crm.Segmentation.Application.Segments;
using Crm.Segmentation.Domain.Rules;
using Crm.Segmentation.Infrastructure.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Crm.Segmentation.Infrastructure.Preview;

/// <summary>
/// POST /segments/preview (task-6 brief): parse -&gt; compile -&gt; count, behind a 60 s Redis cache keyed by the
/// tenant and the canonical definition hash, with a five-second command budget. A Redis problem is a cache miss,
/// never a failed preview (ruling E12, mirrors RedisIdentityCache); a SQL or command timeout is reported to the
/// caller as <c>{ count: null, timedOut: true }</c> and is never cached.
/// </summary>
internal sealed class PreviewService(
    ITenantContext tenant,
    RuleCompiler compiler,
    CompiledQueryExecutor executor,
    IConnectionMultiplexer redis,
    IClock clock,
    ILogger<PreviewService> logger)
{
    private static readonly TimeSpan CommandBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    public async Task<Result<PreviewResponse>> PreviewAsync(JsonElement definition, CancellationToken cancellationToken)
    {
        var parsed = RuleParser.Parse(definition);
        if (!parsed.IsValid)
        {
            return SegmentErrors.InvalidDefinition(parsed.Errors);
        }

        var tree = parsed.Tree!;
        var tenantId = tenant.TenantId;
        var hashHex = DefinitionHash.ToHex(DefinitionHash.Compute(tree));
        var cacheKey = RedisKeys.PreviewCount(tenantId, hashHex);

        var cached = await TryGetCachedAsync(cacheKey);
        if (cached is not null)
        {
            return new PreviewResponse(cached.Count, false, cached.CachedAt);
        }

        var compiled = compiler.Compile(tree, tenantId);

        int count;
        try
        {
            count = await executor.CountAsync(compiled, CommandBudget, cancellationToken);
        }
        catch (SqlException ex) when (ex.Number == -2)
        {
            return new PreviewResponse(null, true, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own command-budget token fired, not the caller's; the caller's own cancellation propagates unchanged.
            return new PreviewResponse(null, true, null);
        }

        var cachedAt = clock.UtcNow;
        await TrySetCachedAsync(cacheKey, count, cachedAt);

        return new PreviewResponse(count, false, cachedAt);
    }

    private async Task<CachedPreviewCount?> TryGetCachedAsync(string cacheKey)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(cacheKey);
            return value.HasValue ? JsonSerializer.Deserialize<CachedPreviewCount>((string)value!) : null;
        }
        catch (Exception ex) when (IsRedisFailure(ex) || ex is JsonException)
        {
            // A transport failure and a corrupted/partial cached value are both a soft miss (ruling E12):
            // recompute and overwrite the entry, never fail the preview over a cache-read problem.
            logger.LogWarning(ex, "Preview cache read failed; recomputing");
            return null;
        }
    }

    private async Task TrySetCachedAsync(string cacheKey, int count, DateTime cachedAt)
    {
        try
        {
            var json = JsonSerializer.Serialize(new CachedPreviewCount(count, cachedAt));
            await redis.GetDatabase().StringSetAsync(cacheKey, json, CacheTtl);
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            logger.LogWarning(ex, "Preview cache write failed; the next preview recomputes");
        }
    }

    private static bool IsRedisFailure(Exception ex) => ex is RedisException or TimeoutException;

    private sealed record CachedPreviewCount(
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("cachedAt")] DateTime CachedAt);
}
