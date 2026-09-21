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
