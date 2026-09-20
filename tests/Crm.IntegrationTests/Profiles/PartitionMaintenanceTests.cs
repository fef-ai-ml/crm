using Crm.IntegrationTests.Fixtures;
using Crm.Profiles.Infrastructure.Maintenance;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Crm.IntegrationTests.Profiles;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class PartitionMaintenanceTests(CrmStackFixture stack)
{
    [Fact]
    public async Task Missing_month_boundaries_are_added_once()
    {
        var service = stack.Worker.Services.GetServices<IHostedService>().OfType<EventPartitionMaintenanceService>().Single();
        var farFuture = new DateTime(2031, 5, 17, 0, 0, 0, DateTimeKind.Utc); // beyond the migration's static boundaries

        var added = await service.EnsureAsync(farFuture, CancellationToken.None);
        var addedAgain = await service.EnsureAsync(farFuture, CancellationToken.None);

        added.Should().Be(3, "2031-05, 2031-06 and 2031-07 were missing");
        addedAgain.Should().Be(0, "the job is idempotent");
        (await BoundaryExistsAsync(new DateTime(2031, 7, 1))).Should().BeTrue();
    }

    private async Task<bool> BoundaryExistsAsync(DateTime boundary)
    {
        await using var connection = new SqlConnection(stack.SqlConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM sys.partition_range_values v
            JOIN sys.partition_functions f ON f.function_id = v.function_id
            WHERE f.name = N'PF_Events_Monthly' AND CAST(v.value AS datetime2(3)) = @boundary
            """;
        command.Parameters.Add(new SqlParameter("@boundary", boundary));
        return (int)(await command.ExecuteScalarAsync())! == 1;
    }
}
