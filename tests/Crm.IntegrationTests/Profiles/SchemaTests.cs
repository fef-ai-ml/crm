using Crm.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Crm.IntegrationTests.Profiles;

/// <summary>The migration must produce what docs/data-dictionary/profiles.md names; EF cannot express partitioning, so it is checked in the catalog.</summary>
[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public class SchemaTests(CrmStackFixture stack)
{
    [Fact]
    public async Task Events_and_all_of_its_indexes_live_on_the_monthly_partition_scheme()
    {
        var rows = await QueryAsync(
            """
            SELECT i.name + '|' + ds.name + '|' + CAST(i.is_unique AS char(1))
            FROM sys.indexes i
            JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
            WHERE i.object_id = OBJECT_ID(N'profiles.Events') AND i.name IS NOT NULL
            ORDER BY i.name
            """);

        rows.Should().Equal(
            "CX_Events_TenantId_ProfileId_OccurredAt|PS_Events_Monthly|0",
            "IX_Events_TenantId_Name_OccurredAt|PS_Events_Monthly|0",
            "UX_Events_Id|PS_Events_Monthly|1");
    }

    [Fact]
    public async Task Partition_function_is_monthly_range_right_with_at_least_two_years_of_boundaries()
    {
        var rows = await QueryAsync(
            """
            SELECT CAST(f.boundary_value_on_right AS char(1)) + '|' + CAST(COUNT(v.boundary_id) AS varchar(10))
            FROM sys.partition_functions f
            JOIN sys.partition_range_values v ON v.function_id = f.function_id
            WHERE f.name = N'PF_Events_Monthly'
            GROUP BY f.boundary_value_on_right
            """);

        rows.Should().ContainSingle();
        var parts = rows[0].Split('|');
        parts[0].Should().Be("1");
        int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture).Should().BeGreaterThanOrEqualTo(24);
    }

    [Fact]
    public async Task Profiles_has_the_documented_indexes_constraints_and_promoted_columns()
    {
        var indexes = await QueryAsync(
            """
            SELECT i.name + '|' + i.type_desc COLLATE DATABASE_DEFAULT + '|' + CAST(i.is_unique AS char(1)) + '|' + CAST(i.has_filter AS char(1))
            FROM sys.indexes i WHERE i.object_id = OBJECT_ID(N'profiles.Profiles') ORDER BY i.name
            """);
        indexes.Should().Equal(
            "CX_Profiles_TenantId_Id|CLUSTERED|1|0",
            "IX_Profiles_TenantId_Attr_city|NONCLUSTERED|0|0",
            "IX_Profiles_TenantId_Attr_country|NONCLUSTERED|0|0",
            "IX_Profiles_TenantId_Attr_plan|NONCLUSTERED|0|0",
            "IX_Profiles_TenantId_LastSeenAt_Id|NONCLUSTERED|0|1",
            "PK_Profiles|NONCLUSTERED|1|0",
            "UX_Profiles_TenantId_Email|NONCLUSTERED|1|1",
            "UX_Profiles_TenantId_ExternalId|NONCLUSTERED|1|1",
            "UX_Profiles_TenantId_Phone|NONCLUSTERED|1|1");

        // Keyset index of GET /v1/profiles (plan G, ruling G2): key columns TenantId, LastSeenAt DESC, Id DESC,
        // then the INCLUDEd projection columns.
        var keyset = await QueryAsync(
            """
            SELECT c.name + '|' + CAST(ic.key_ordinal AS varchar(2)) + '|' + CAST(ic.is_descending_key AS char(1)) + '|' + CAST(ic.is_included_column AS char(1))
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'profiles.Profiles') AND i.name = N'IX_Profiles_TenantId_LastSeenAt_Id'
            ORDER BY ic.is_included_column, ic.key_ordinal, c.name
            """);
        keyset.Should().Equal(
            "TenantId|1|0|0", "LastSeenAt|2|1|0", "Id|3|1|0",
            "Email|0|0|1", "EventCount|0|0|1", "ExternalId|0|0|1", "MergedIntoId|0|0|1", "Phone|0|0|1");

        var computed = await QueryAsync(
            "SELECT name + '|' + CAST(is_persisted AS char(1)) FROM sys.computed_columns WHERE object_id = OBJECT_ID(N'profiles.Profiles') ORDER BY name");
        computed.Should().Equal("Attr_city|1", "Attr_country|1", "Attr_plan|1");

        var constraints = await QueryAsync(
            """
            SELECT name FROM sys.check_constraints WHERE parent_object_id IN (OBJECT_ID(N'profiles.Profiles'), OBJECT_ID(N'profiles.Events'), OBJECT_ID(N'profiles.UserIdentities'))
            UNION ALL
            SELECT name FROM sys.foreign_keys WHERE parent_object_id IN (OBJECT_ID(N'profiles.Profiles'), OBJECT_ID(N'profiles.Events'), OBJECT_ID(N'profiles.UserIdentities'))
            ORDER BY name
            """);
        constraints.Should().Equal(
            "CK_Events_Properties_IsJson",
            "CK_Profiles_Attributes_IsJson",
            "CK_UserIdentities_Kind",
            "FK_Events_ProfileId",
            "FK_Profiles_MergedIntoId",
            "FK_UserIdentities_ProfileId");
    }

    [Fact]
    public async Task UserIdentities_is_keyed_by_tenant_kind_and_value()
    {
        var rows = await QueryAsync(
            """
            SELECT i.name + '|' + i.type_desc COLLATE DATABASE_DEFAULT + '|' + STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'profiles.UserIdentities')
            GROUP BY i.name, i.type_desc ORDER BY i.name
            """);

        rows.Should().Equal(
            "IX_UserIdentities_ProfileId|NONCLUSTERED|ProfileId",
            "PK_UserIdentities|CLUSTERED|TenantId,Kind,Value");
    }

    [Fact]
    public async Task Migration_history_is_kept_per_module()
    {
        var rows = await QueryAsync("SELECT CAST(COUNT(*) AS varchar(10)) FROM profiles.__EFMigrationsHistory_profiles");
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
