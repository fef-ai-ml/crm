using Crm.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Crm.IntegrationTests.Tenancy;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public class SchemaTests(CrmStackFixture stack)
{
    [Fact]
    public async Task Migration_creates_identity_tables_indexes_roles_and_snapshot_isolation()
    {
        await using var connection = new SqlConnection(stack.SqlConnectionString);
        await connection.OpenAsync();

        var indexes = await ReadStringsAsync(connection,
            "SELECT i.name FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'identity' AND i.name IS NOT NULL");
        indexes.Should().Contain(["UX_Tenants_Slug", "UX_ApiKeys_Prefix", "IX_ApiKeys_TenantId", "IX_AspNetUsers_TenantId", "UX_RefreshTokens_TokenHash", "IX_RefreshTokens_UserId"]);

        var roles = await ReadStringsAsync(connection, "SELECT Name FROM [identity].AspNetRoles ORDER BY Name");
        roles.Should().Equal("Admin", "Viewer");

        var snapshot = await ReadStringsAsync(connection, "SELECT CAST(is_read_committed_snapshot_on AS varchar(1)) FROM sys.databases WHERE name = DB_NAME()");
        snapshot.Should().Equal("1");
    }

    private static async Task<List<string>> ReadStringsAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
