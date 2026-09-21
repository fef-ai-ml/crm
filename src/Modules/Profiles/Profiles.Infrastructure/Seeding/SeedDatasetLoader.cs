using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Crm.Kernel;
using Crm.Kernel.Time;
using Crm.Profiles.Application.Seeding;
using Crm.Profiles.Domain;
using Microsoft.Data.SqlClient;

namespace Crm.Profiles.Infrastructure.Seeding;

public sealed record SeedLoadResult(string Status, int Profiles, int MergedProfiles, int Events, IReadOnlyDictionary<string, int> Segments, long ElapsedMilliseconds);

/// <summary>
/// Loads the demo dataset into one tenant with SqlBulkCopy (ruling F12: the pipeline cannot move 100k events inside the
/// make-seed budget). One transaction: bulk copy, then the six documented segment counts are verified with plain SQL and
/// the whole load is rolled back when one differs (ruling F15). A tenant that already has the pinned profile is left
/// untouched. Raw SQL: every statement carries TenantId itself.
/// </summary>
public sealed class SeedDatasetLoader(string connectionString, IClock clock)
{
    public const string CountMismatchCode = "seed.count_mismatch";

    private const string CountsSql = """
        SELECT
          (SELECT COUNT(*) FROM profiles.Profiles p WHERE p.TenantId = @t AND p.MergedIntoId IS NULL) AS [profiles],
          (SELECT COUNT(*) FROM profiles.Profiles p WHERE p.TenantId = @t AND p.MergedIntoId IS NOT NULL) AS [merged],
          (SELECT COUNT(*) FROM profiles.Events e WHERE e.TenantId = @t) AS [events],
          (SELECT COUNT(*) FROM profiles.Profiles p WHERE p.TenantId = @t AND p.MergedIntoId IS NULL AND p.Attr_plan = N'pro'
              AND (p.Attr_country IN (N'ID', N'SG') OR NOT EXISTS (SELECT 1 FROM profiles.Events e WHERE e.TenantId = @t AND e.ProfileId = p.Id AND e.Name = N'app_open' AND e.OccurredAt >= DATEADD(day, -7, @now)))) AS [seed-pro-id],
          (SELECT COUNT(*) FROM profiles.Profiles p WHERE p.TenantId = @t AND p.MergedIntoId IS NULL
              AND EXISTS (SELECT 1 FROM profiles.Events e WHERE e.TenantId = @t AND e.ProfileId = p.Id AND e.Name = N'purchase' AND e.OccurredAt >= DATEADD(day, -30, @now))) AS [seed-purchasers],
          (SELECT COUNT(*) FROM profiles.Profiles p WHERE p.TenantId = @t AND p.MergedIntoId IS NULL
              AND NOT EXISTS (SELECT 1 FROM profiles.Events e WHERE e.TenantId = @t AND e.ProfileId = p.Id AND e.Name = N'app_open' AND e.OccurredAt >= DATEADD(day, -7, @now))) AS [seed-dormant],
          (SELECT COUNT(*) FROM profiles.Profiles p WHERE p.TenantId = @t AND p.MergedIntoId IS NULL AND p.Attr_plan IS NULL) AS [seed-no-plan],
          (SELECT COUNT(*) FROM profiles.Profiles p WHERE p.TenantId = @t AND p.MergedIntoId IS NULL AND (p.Attr_plan IS NULL OR p.Attr_plan <> N'pro')) AS [seed-not-pro],
          (SELECT COUNT(*) FROM profiles.Profiles p WHERE p.TenantId = @t AND p.MergedIntoId IS NULL AND p.Attr_country = N'SG'
              AND TRY_CONVERT(decimal(18, 2), JSON_VALUE(p.Attributes, '$.ltv')) >= 150) AS [seed-high-ltv-sg];
        """;

    public async Task<Result<SeedLoadResult>> LoadAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var now = Truncate(clock.UtcNow);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (await ExistsAsync(connection, tenantId, cancellationToken))
        {
            var current = await CountAsync(connection, null, tenantId, now, cancellationToken);
            return ToResult("exists", current, watch);
        }

        var data = SeedDatasetGenerator.Generate();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        // A tenant that already has profiles (but not the pinned one) is not empty, so the six documented counts
        // are verified as a DELTA of what this load added, not as an absolute count (I2). A pre-existing
        // profile's segment membership is identical before and after (same @now on both sides), so the delta is
        // exact regardless of what else is in the tenant.
        var before = await CountAsync(connection, transaction, tenantId, now, cancellationToken);

        using var profiles = Table(("Id", typeof(Guid)), ("TenantId", typeof(Guid)), ("ExternalId", typeof(string)), ("Email", typeof(string)), ("Phone", typeof(string)),
            ("Attributes", typeof(string)), ("FirstSeenAt", typeof(DateTime)), ("LastSeenAt", typeof(DateTime)), ("EventCount", typeof(long)),
            ("MergedIntoId", typeof(Guid)), ("CreatedAt", typeof(DateTime)), ("UpdatedAt", typeof(DateTime)));
        using var identities = Table(("TenantId", typeof(Guid)), ("Kind", typeof(string)), ("Value", typeof(string)), ("ProfileId", typeof(Guid)), ("CreatedAt", typeof(DateTime)), ("UpdatedAt", typeof(DateTime)));
        using var events = Table(("Id", typeof(Guid)), ("TenantId", typeof(Guid)), ("ProfileId", typeof(Guid)), ("Name", typeof(string)), ("Properties", typeof(string)),
            ("OccurredAt", typeof(DateTime)), ("ReceivedAt", typeof(DateTime)), ("CreatedAt", typeof(DateTime)), ("UpdatedAt", typeof(DateTime)));
        Populate(tenantId, now, data, profiles, identities, events);

        await CopyAsync(connection, transaction, "profiles.Profiles", profiles, cancellationToken);
        await CopyAsync(connection, transaction, "profiles.UserIdentities", identities, cancellationToken);
        await CopyAsync(connection, transaction, "profiles.Events", events, cancellationToken);

        var after = await CountAsync(connection, transaction, tenantId, now, cancellationToken);
        var delta = after.ToDictionary(c => c.Key, c => c.Value - before[c.Key], StringComparer.Ordinal);
        var wrong = SeedExpectations.Segments.Where(s => delta[s.Key] != s.Value)
            .Select(s => $"{s.Key}: expected delta {s.Value}, found delta {delta[s.Key]}")
            .ToList();
        if (delta["profiles"] != SeedExpectations.Profiles || delta["merged"] != SeedExpectations.MergedProfiles || delta["events"] != SeedExpectations.Events)
        {
            wrong.Add($"delta rows: {delta["profiles"]} profiles, {delta["merged"]} merged, {delta["events"]} events");
        }

        if (wrong.Count > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Error.Unexpected(CountMismatchCode, "The seed dataset's delta does not match docs/test/fixtures.md; nothing was loaded. " + string.Join("; ", wrong));
        }

        await transaction.CommitAsync(cancellationToken);
        return ToResult("loaded", after, watch);
    }

    private static Result<SeedLoadResult> ToResult(string status, Dictionary<string, int> counts, Stopwatch watch)
        => new SeedLoadResult(
            status, counts["profiles"], counts["merged"], counts["events"],
            SeedExpectations.Segments.Keys.ToDictionary(k => k, k => counts[k], StringComparer.Ordinal),
            watch.ElapsedMilliseconds);

    private static async Task<bool> ExistsAsync(SqlConnection connection, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT COUNT(*) FROM profiles.Profiles WHERE TenantId = @t AND ExternalId = @id", connection);
        command.Parameters.AddWithValue("@t", tenantId);
        command.Parameters.AddWithValue("@id", SeedExpectations.PinnedExternalId);
        return (int)(await command.ExecuteScalarAsync(cancellationToken))! > 0;
    }

    private static async Task<Dictionary<string, int>> CountAsync(SqlConnection connection, SqlTransaction? transaction, Guid tenantId, DateTime now, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(CountsSql, connection, transaction) { CommandTimeout = 120 };
        command.Parameters.AddWithValue("@t", tenantId);
        command.Parameters.Add("@now", SqlDbType.DateTime2).Value = now;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            counts[reader.GetName(i)] = reader.GetInt32(i);
        }

        return counts;
    }

    private static async Task CopyAsync(SqlConnection connection, SqlTransaction transaction, string table, DataTable rows, CancellationToken cancellationToken)
    {
        // Smaller batches reduce, but do not eliminate, lock escalation: they keep any one statement under the
        // ~5,000-lock escalation threshold, but the whole load (profiles + identities + events) runs in one
        // transaction, and locks are held until commit — so the transaction's cumulative lock footprint still
        // escalates profiles.Events to a table lock for the duration of the ~10 s load, across all tenants (I9).
        using var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, transaction)
        {
            DestinationTableName = table,
            BatchSize = 2_000,
            BulkCopyTimeout = 120,
        };
        foreach (DataColumn column in rows.Columns)
        {
            copy.ColumnMappings.Add(column.ColumnName, column.ColumnName); // by name: the computed Attr_* columns stay unmapped
        }

        await copy.WriteToServerAsync(rows, cancellationToken);
    }

    private static void Populate(Guid tenantId, DateTime now, SeedData data, DataTable profiles, DataTable identities, DataTable events)
    {
        var byProfile = data.Events.ToLookup(e => e.ProfileIndex);
        var ids = new Dictionary<int, (Guid Id, DateTime FirstSeenAt)>(data.Profiles.Count);

        foreach (var profile in data.Profiles)
        {
            var times = byProfile[profile.Index].Select(e => now - e.Age).ToList();
            var first = times.Count > 0 ? times.Min() : now.AddDays(-(30 + (profile.Index % 60)));
            var last = times.Count > 0 ? times.Max() : first;
            var id = Guid.CreateVersion7(new DateTimeOffset(first));
            ids[profile.Index] = (id, first);

            profiles.Rows.Add(id, tenantId, profile.ExternalId, profile.Email, (object?)profile.Phone ?? DBNull.Value,
                Attributes(profile), first, last, (long)times.Count, DBNull.Value, first, last);

            identities.Rows.Add(tenantId, IdentityKinds.ExternalId, profile.ExternalId, id, first, first);
            identities.Rows.Add(tenantId, IdentityKinds.Email, profile.Email, id, first, first);
            if (profile.Phone is not null)
            {
                identities.Rows.Add(tenantId, IdentityKinds.Phone, profile.Phone, id, first, first);
            }
        }

        foreach (var e in data.Events)
        {
            var at = now - e.Age;
            var properties = e.Amount is { } amount ? $"{{\"amount\":{amount.ToString("0.00", CultureInfo.InvariantCulture)}}}" : "{}";
            events.Rows.Add(Guid.CreateVersion7(new DateTimeOffset(at)), tenantId, ids[e.ProfileIndex].Id, e.Name, properties, at, at, at, at);
        }

        // What the pipeline leaves behind after identify(userId, anonymousId): a loser row without contact points that
        // points at the winner, and the anonymous identity re-pointed to the winner.
        foreach (var merge in data.Merges)
        {
            var (winnerId, winnerFirstSeen) = ids[merge.WinnerIndex];
            var mergedAt = winnerFirstSeen.AddMinutes(merge.Number);
            profiles.Rows.Add(Guid.CreateVersion7(new DateTimeOffset(winnerFirstSeen)), tenantId, DBNull.Value, DBNull.Value, DBNull.Value,
                "{}", winnerFirstSeen, winnerFirstSeen, 0L, winnerId, winnerFirstSeen, mergedAt);
            identities.Rows.Add(tenantId, IdentityKinds.AnonymousId, merge.AnonymousId, winnerId, winnerFirstSeen, mergedAt);
        }
    }

    private static string Attributes(SeedProfile profile)
    {
        var attributes = new Dictionary<string, object>(StringComparer.Ordinal) { ["email"] = profile.Email };
        if (profile.Phone is not null)
        {
            attributes["phone"] = profile.Phone;
        }

        if (profile.Plan is not null)
        {
            attributes["plan"] = profile.Plan;
        }

        if (profile.Country is not null)
        {
            attributes["country"] = profile.Country;
        }

        if (profile.City is not null)
        {
            attributes["city"] = profile.City;
        }

        if (profile.OptedIn is { } optedIn)
        {
            attributes["opted_in"] = optedIn;
        }

        if (profile.Ltv is { } ltv)
        {
            attributes["ltv"] = ltv;
        }

        return JsonSerializer.Serialize(attributes);
    }

    private static DataTable Table(params (string Name, Type Type)[] columns)
    {
        var table = new DataTable { Locale = CultureInfo.InvariantCulture };
        foreach (var (name, type) in columns)
        {
            table.Columns.Add(name, type);
        }

        return table;
    }

    private static DateTime Truncate(DateTime utc)
        => new(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
}
