using Crm.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Crm.IntegrationTests.Segments;

/// <summary>
/// The migration must produce exactly what docs/data-dictionary/segments.md and plan H's rulings (H9/H10/H11)
/// describe; EF's model does not surface descending flags, INCLUDE columns or check constraints, so those are
/// checked in the catalog, same style as tests/Crm.IntegrationTests/Profiles/SchemaTests.cs.
/// </summary>
[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public class SegmentsSchemaTests(CrmStackFixture stack)
{
    [Fact]
    public async Task Segments_has_the_documented_indexes()
    {
        // IX_Segments_CurrentRunId is EF's automatic index for FK_Segments_CurrentRunId: CurrentRunId is not a
        // leading key column of any other index on this table, so the ForeignKeyIndexConvention adds its own.
        var indexes = await QueryAsync(
            """
            SELECT i.name + '|' + i.type_desc COLLATE DATABASE_DEFAULT + '|' + CAST(i.is_unique AS char(1)) + '|' + CAST(i.has_filter AS char(1))
            FROM sys.indexes i WHERE i.object_id = OBJECT_ID(N'segments.Segments') AND i.name IS NOT NULL ORDER BY i.name
            """);

        indexes.Should().Equal(
            "CX_Segments_TenantId_Id|CLUSTERED|1|0",
            "IX_Segments_CurrentRunId|NONCLUSTERED|0|0",
            "IX_Segments_TenantId_CreatedAt_Id|NONCLUSTERED|0|0",
            "PK_Segments|NONCLUSTERED|1|0",
            "UX_Segments_TenantId_Name|NONCLUSTERED|1|0");
    }

    [Fact]
    public async Task Segments_keyset_index_is_TenantId_CreatedAt_desc_Id_desc_with_the_list_projection_included()
    {
        var columns = await QueryAsync(
            """
            SELECT c.name + '|' + CAST(ic.key_ordinal AS varchar(2)) + '|' + CAST(ic.is_descending_key AS char(1)) + '|' + CAST(ic.is_included_column AS char(1))
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'segments.Segments') AND i.name = N'IX_Segments_TenantId_CreatedAt_Id'
            ORDER BY ic.is_included_column, ic.key_ordinal, c.name
            """);

        columns.Should().Equal(
            "TenantId|1|0|0", "CreatedAt|2|1|0", "Id|3|1|0",
            "IsRealtimeEligible|0|0|1", "LastRunAt|0|0|1", "MemberCount|0|0|1", "Name|0|0|1", "Status|0|0|1");
    }

    [Fact]
    public async Task SegmentRuns_has_the_documented_indexes_and_its_key_column_is_named_RunId()
    {
        // IX_SegmentRuns_SegmentId is EF's automatic index for FK_SegmentRuns_SegmentId: SegmentId is the second
        // key column of IX_SegmentRuns_TenantId_SegmentId_CreatedAt_RunId, not a leading column, so it does not count.
        var indexes = await QueryAsync(
            """
            SELECT i.name + '|' + i.type_desc COLLATE DATABASE_DEFAULT + '|' + CAST(i.is_unique AS char(1))
            FROM sys.indexes i WHERE i.object_id = OBJECT_ID(N'segments.SegmentRuns') AND i.name IS NOT NULL ORDER BY i.name
            """);

        indexes.Should().Equal(
            "IX_SegmentRuns_SegmentId|NONCLUSTERED|0",
            "IX_SegmentRuns_TenantId_SegmentId_CreatedAt_RunId|NONCLUSTERED|0",
            "PK_SegmentRuns|CLUSTERED|1");

        (await QueryAsync("SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(N'segments.SegmentRuns') AND column_id = 1"))
            .Should().Equal("RunId");
    }

    [Fact]
    public async Task SegmentRuns_keyset_index_is_TenantId_SegmentId_CreatedAt_desc_RunId_desc()
    {
        var columns = await QueryAsync(
            """
            SELECT c.name + '|' + CAST(ic.key_ordinal AS varchar(2)) + '|' + CAST(ic.is_descending_key AS char(1))
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'segments.SegmentRuns') AND i.name = N'IX_SegmentRuns_TenantId_SegmentId_CreatedAt_RunId'
            ORDER BY ic.key_ordinal
            """);

        columns.Should().Equal("TenantId|1|0", "SegmentId|2|0", "CreatedAt|3|1", "RunId|4|1");
    }

    [Fact]
    public async Task SegmentRunMembers_is_keyed_by_run_and_profile_has_exactly_three_columns_and_cascades_from_its_run()
    {
        var indexes = await QueryAsync(
            """
            SELECT i.name + '|' + i.type_desc COLLATE DATABASE_DEFAULT + '|' + STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'segments.SegmentRunMembers')
            GROUP BY i.name, i.type_desc ORDER BY i.name
            """);

        indexes.Should().Equal(
            "IX_SegmentRunMembers_TenantId_ProfileId|NONCLUSTERED|TenantId,ProfileId",
            "PK_SegmentRunMembers|CLUSTERED|RunId,ProfileId");

        // EF emits primary-key properties first regardless of declaration order, so the physical column order is
        // the key (RunId, ProfileId) followed by TenantId; the set is what "exactly TenantId, RunId, ProfileId" means.
        (await QueryAsync("SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(N'segments.SegmentRunMembers') ORDER BY column_id"))
            .Should().Equal("RunId", "ProfileId", "TenantId");

        (await QueryAsync("SELECT delete_referential_action_desc FROM sys.foreign_keys WHERE name = N'FK_SegmentRunMembers_RunId'"))
            .Should().Equal("CASCADE");
    }

    [Fact]
    public async Task MembershipChanges_has_the_documented_indexes()
    {
        var indexes = await QueryAsync(
            """
            SELECT i.name + '|' + i.type_desc COLLATE DATABASE_DEFAULT + '|' + CAST(i.is_unique AS char(1))
            FROM sys.indexes i WHERE i.object_id = OBJECT_ID(N'segments.MembershipChanges') AND i.name IS NOT NULL ORDER BY i.name
            """);

        indexes.Should().Equal(
            "CX_MembershipChanges_TenantId_SegmentId_OccurredAt_Id|CLUSTERED|1",
            "IX_MembershipChanges_TenantId_ProfileId|NONCLUSTERED|0",
            "PK_MembershipChanges|NONCLUSTERED|1");
    }

    [Fact]
    public async Task MembershipChanges_unique_clustered_index_is_TenantId_SegmentId_OccurredAt_desc_Id_desc()
    {
        var columns = await QueryAsync(
            """
            SELECT c.name + '|' + CAST(ic.key_ordinal AS varchar(2)) + '|' + CAST(ic.is_descending_key AS char(1))
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'segments.MembershipChanges') AND i.name = N'CX_MembershipChanges_TenantId_SegmentId_OccurredAt_Id'
            ORDER BY ic.key_ordinal
            """);

        columns.Should().Equal("TenantId|1|0", "SegmentId|2|0", "OccurredAt|3|1", "Id|4|1");
    }

    [Fact]
    public async Task The_check_constraints_and_foreign_keys_across_all_four_tables_are_exactly_as_documented()
    {
        // Six check constraints (two per table on Segments and SegmentRuns, two on MembershipChanges) and the
        // three FKs the brief calls for; MembershipChanges.SegmentId has no FK (see MembershipChangeConfiguration).
        var names = await QueryAsync(
            """
            SELECT name FROM sys.check_constraints WHERE parent_object_id IN
                (OBJECT_ID(N'segments.Segments'), OBJECT_ID(N'segments.SegmentRuns'), OBJECT_ID(N'segments.SegmentRunMembers'), OBJECT_ID(N'segments.MembershipChanges'))
            UNION ALL
            SELECT name FROM sys.foreign_keys WHERE parent_object_id IN
                (OBJECT_ID(N'segments.Segments'), OBJECT_ID(N'segments.SegmentRuns'), OBJECT_ID(N'segments.SegmentRunMembers'), OBJECT_ID(N'segments.MembershipChanges'))
            ORDER BY name
            """);

        names.Should().Equal(
            "CK_MembershipChanges_Change",
            "CK_MembershipChanges_Source",
            "CK_SegmentRuns_Status",
            "CK_SegmentRuns_Trigger",
            "CK_Segments_Definition_IsJson",
            "CK_Segments_Status",
            "FK_SegmentRunMembers_RunId",
            "FK_SegmentRuns_SegmentId",
            "FK_Segments_CurrentRunId");
    }

    [Fact]
    public async Task Migration_history_is_kept_per_module()
    {
        var rows = await QueryAsync("SELECT CAST(COUNT(*) AS varchar(10)) FROM segments.__EFMigrationsHistory_segments");
        int.Parse(rows[0], System.Globalization.CultureInfo.InvariantCulture).Should().BeGreaterThanOrEqualTo(1);
    }

    private async Task<List<string>> QueryAsync(string sql)
    {
        await using var connection = new SqlConnection(stack.SqlConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
