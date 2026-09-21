using System.Diagnostics.CodeAnalysis;
using Crm.Profiles.Infrastructure.Seeding;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>docs/test/fixtures.md: the demo dataset for a test tenant, loaded by the same code `make seed` reaches through the API.</summary>
internal static class SeedDataset
{
    public static async Task<SeedLoadResult> LoadAsync(CrmApiFactory factory, Guid tenantId)
    {
        var loader = factory.Services.GetRequiredService<SeedDatasetLoader>();
        return (await loader.LoadAsync(tenantId, CancellationToken.None)).Value;
    }

    /// <summary>Removes a seeded tenant's rows so 105k rows do not stay in the shared database for the rest of the suite.</summary>
    [SuppressMessage("Microsoft.Security", "CA2100", Justification = "Table names are compile-time constants, not input.")]
    public static async Task DeleteAsync(string connectionString, Guid tenantId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var table in new[] { "profiles.Events", "profiles.UserIdentities", "profiles.Profiles" })
        {
            await using var command = new SqlCommand($"DELETE FROM {table} WHERE TenantId = @t", connection) { CommandTimeout = 120 };
            command.Parameters.AddWithValue("@t", tenantId);
            await command.ExecuteNonQueryAsync();
        }
    }
}
