using System.Data;
using Microsoft.Data.SqlClient;

namespace Crm.IntegrationTests.Fixtures;

public sealed record ProfileRow(Guid Id, string? ExternalId, string? Email, string? Phone, string Attributes, DateTime FirstSeenAt, DateTime LastSeenAt, long EventCount, Guid? MergedIntoId);

public sealed record EventRow(Guid Id, Guid ProfileId, string Name, DateTime OccurredAt);

/// <summary>Reads schema `profiles` with plain SQL: plan E has no read API, and assertions should not share code with what they check.</summary>
public static class ProfilesDb
{
    public static Task<ProfileRow?> FindByIdentityAsync(string connectionString, Guid tenantId, string kind, string value)
        => SingleProfileAsync(
            connectionString,
            "SELECT p.Id, p.ExternalId, p.Email, p.Phone, p.Attributes, p.FirstSeenAt, p.LastSeenAt, p.EventCount, p.MergedIntoId " +
            "FROM profiles.UserIdentities i JOIN profiles.Profiles p ON p.Id = i.ProfileId WHERE i.TenantId = @tenant AND i.Kind = @kind AND i.Value = @value",
            new SqlParameter("@tenant", tenantId), new SqlParameter("@kind", kind), new SqlParameter("@value", value));

    public static Task<ProfileRow?> GetAsync(string connectionString, Guid tenantId, Guid profileId)
        => SingleProfileAsync(
            connectionString,
            "SELECT p.Id, p.ExternalId, p.Email, p.Phone, p.Attributes, p.FirstSeenAt, p.LastSeenAt, p.EventCount, p.MergedIntoId " +
            "FROM profiles.Profiles p WHERE p.TenantId = @tenant AND p.Id = @id",
            new SqlParameter("@tenant", tenantId), new SqlParameter("@id", profileId));

    public static async Task<int> CountProfilesAsync(string connectionString, Guid tenantId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM profiles.Profiles WHERE TenantId = @tenant";
        command.Parameters.Add(new SqlParameter("@tenant", tenantId));
        return (int)(await command.ExecuteScalarAsync())!;
    }

    public static async Task<IReadOnlyList<EventRow>> EventsAsync(string connectionString, Guid tenantId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ProfileId, Name, OccurredAt FROM profiles.Events WHERE TenantId = @tenant ORDER BY OccurredAt, Id";
        command.Parameters.Add(new SqlParameter("@tenant", tenantId));
        var rows = new List<EventRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new EventRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetDateTime(3)));
        }

        return rows;
    }

    /// <summary>
    /// Inserts a profile directly, bypassing the pipeline: for tests that need a profile to exist without
    /// waiting on Kafka/the Worker (I1, I4). One transaction, raw SQL, parameters only.
    /// </summary>
    public static async Task<Guid> InsertProfileAsync(string connectionString, Guid tenantId, string externalId, DateTime lastSeenAtUtc)
    {
        var at = new DateTime(lastSeenAtUtc.Ticks - (lastSeenAtUtc.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
        var id = Guid.CreateVersion7(new DateTimeOffset(at));

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO profiles.Profiles (Id, TenantId, ExternalId, Attributes, FirstSeenAt, LastSeenAt, EventCount, CreatedAt, UpdatedAt) " +
                "VALUES (@id, @tenant, @externalId, N'{}', @at, @at, 0, @at, @at)";
            command.Parameters.Add(new SqlParameter("@id", id));
            command.Parameters.Add(new SqlParameter("@tenant", tenantId));
            command.Parameters.Add(new SqlParameter("@externalId", externalId));
            command.Parameters.Add(new SqlParameter("@at", SqlDbType.DateTime2) { Value = at });
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO profiles.UserIdentities (TenantId, Kind, Value, ProfileId, CreatedAt, UpdatedAt) " +
                "VALUES (@tenant, 'external_id', @externalId, @id, @at, @at)";
            command.Parameters.Add(new SqlParameter("@tenant", tenantId));
            command.Parameters.Add(new SqlParameter("@externalId", externalId));
            command.Parameters.Add(new SqlParameter("@id", id));
            command.Parameters.Add(new SqlParameter("@at", SqlDbType.DateTime2) { Value = at });
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return id;
    }

    private static async Task<ProfileRow?> SingleProfileAsync(string connectionString, string sql, params SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new ProfileRow(
            reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetDateTime(5),
            reader.GetDateTime(6),
            reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetGuid(8));
    }
}
