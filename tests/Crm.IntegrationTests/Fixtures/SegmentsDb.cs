using System.Data;
using Microsoft.Data.SqlClient;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>
/// Inserts directly into schema `segments`, bypassing EF and the tenant interceptor: for tests (IT-031's
/// Segmentation part) that need rows for a tenant already present without exercising the write path under test.
/// </summary>
public static class SegmentsDb
{
    public static async Task<Guid> InsertSegmentAsync(string connectionString, Guid tenantId, string name, DateTime at)
    {
        var id = Guid.CreateVersion7(new DateTimeOffset(at));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO segments.Segments (Id, TenantId, Name, Definition, DefinitionVersion, DefinitionHash, IsRealtimeEligible, Status, MemberCount, CreatedAt, UpdatedAt) " +
            "VALUES (@id, @tenant, @name, N'{}', 1, @hash, 0, 'Draft', 0, @at, @at)";
        command.Parameters.Add(new SqlParameter("@id", id));
        command.Parameters.Add(new SqlParameter("@tenant", tenantId));
        command.Parameters.Add(new SqlParameter("@name", name));
        command.Parameters.Add(new SqlParameter("@hash", SqlDbType.Binary, 32) { Value = new byte[32] });
        command.Parameters.Add(new SqlParameter("@at", SqlDbType.DateTime2) { Value = at });
        await command.ExecuteNonQueryAsync();
        return id;
    }

    public static async Task<Guid> InsertSegmentRunAsync(string connectionString, Guid tenantId, Guid segmentId, DateTime at)
    {
        var id = Guid.CreateVersion7(new DateTimeOffset(at));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO segments.SegmentRuns (RunId, TenantId, SegmentId, Status, [Trigger], CreatedAt, UpdatedAt) " +
            "VALUES (@id, @tenant, @segment, 'Queued', 'Manual', @at, @at)";
        command.Parameters.Add(new SqlParameter("@id", id));
        command.Parameters.Add(new SqlParameter("@tenant", tenantId));
        command.Parameters.Add(new SqlParameter("@segment", segmentId));
        command.Parameters.Add(new SqlParameter("@at", SqlDbType.DateTime2) { Value = at });
        await command.ExecuteNonQueryAsync();
        return id;
    }

    public static async Task InsertSegmentRunMemberAsync(string connectionString, Guid tenantId, Guid runId, Guid profileId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO segments.SegmentRunMembers (TenantId, RunId, ProfileId) VALUES (@tenant, @run, @profile)";
        command.Parameters.Add(new SqlParameter("@tenant", tenantId));
        command.Parameters.Add(new SqlParameter("@run", runId));
        command.Parameters.Add(new SqlParameter("@profile", profileId));
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Task 7's BatchRunTests: reads the columns a batch run swaps, bypassing the API (IT-018 asserts
    /// through this while the members read endpoint does not exist yet).</summary>
    public static async Task<(Guid? CurrentRunId, int MemberCount)> GetSegmentStateAsync(string connectionString, Guid tenantId, Guid segmentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CurrentRunId, MemberCount FROM segments.Segments WHERE TenantId = @tenant AND Id = @id";
        command.Parameters.Add(new SqlParameter("@tenant", tenantId));
        command.Parameters.Add(new SqlParameter("@id", segmentId));
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetInt32(1));
    }

    /// <summary>Newest first (CreatedAt DESC, RunId DESC), mirroring H10 — for IT-019's prune assertion.</summary>
    public static async Task<IReadOnlyList<Guid>> ListRunIdsAsync(string connectionString, Guid tenantId, Guid segmentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT RunId FROM segments.SegmentRuns WHERE TenantId = @tenant AND SegmentId = @segment ORDER BY CreatedAt DESC, RunId DESC";
        command.Parameters.Add(new SqlParameter("@tenant", tenantId));
        command.Parameters.Add(new SqlParameter("@segment", segmentId));
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    public static async Task<int> CountRunMembersAsync(string connectionString, Guid runId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM segments.SegmentRunMembers WHERE RunId = @run";
        command.Parameters.Add(new SqlParameter("@run", runId));
        return (int)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Counts MembershipChanges rows for the given run's finish time (task-7 brief: `OccurredAt` equals
    /// the run's `FinishedAt`) and change kind (`Entered`/`Exited`).</summary>
    public static async Task<int> CountMembershipChangesAsync(string connectionString, Guid tenantId, Guid segmentId, DateTime occurredAt, string change)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM segments.MembershipChanges WHERE TenantId = @tenant AND SegmentId = @segment AND OccurredAt = @at AND Change = @change AND Source = 'Batch'";
        command.Parameters.Add(new SqlParameter("@tenant", tenantId));
        command.Parameters.Add(new SqlParameter("@segment", segmentId));
        command.Parameters.Add(new SqlParameter("@at", SqlDbType.DateTime2) { Value = occurredAt });
        command.Parameters.Add(new SqlParameter("@change", change));
        return (int)(await command.ExecuteScalarAsync())!;
    }

    public static async Task<Guid> InsertMembershipChangeAsync(string connectionString, Guid tenantId, Guid segmentId, Guid profileId, DateTime at)
    {
        var id = Guid.CreateVersion7(new DateTimeOffset(at));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO segments.MembershipChanges (Id, TenantId, SegmentId, ProfileId, Change, Source, OccurredAt) " +
            "VALUES (@id, @tenant, @segment, @profile, 'Entered', 'Batch', @at)";
        command.Parameters.Add(new SqlParameter("@id", id));
        command.Parameters.Add(new SqlParameter("@tenant", tenantId));
        command.Parameters.Add(new SqlParameter("@segment", segmentId));
        command.Parameters.Add(new SqlParameter("@profile", profileId));
        command.Parameters.Add(new SqlParameter("@at", SqlDbType.DateTime2) { Value = at });
        await command.ExecuteNonQueryAsync();
        return id;
    }
}
