using Crm.Kernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Crm.Tenancy.Infrastructure;

/// <summary>Design-time entry point for <c>dotnet ef</c>; connection string from <c>ConnectionStrings__Crm</c>, else the local compose default.</summary>
public sealed class TenancyDbContextFactory : IDesignTimeDbContextFactory<TenancyDbContext>
{
    private const string LocalDefault = "Server=localhost,1433;Database=crm;User Id=sa;Password=Crm_Dev_Passw0rd!;TrustServerCertificate=True;Encrypt=False";

    public TenancyDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Crm");
        var options = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseSqlServer(
                string.IsNullOrWhiteSpace(connectionString) ? LocalDefault : connectionString,
                sql => sql.MigrationsHistoryTable(TenancyDbContext.MigrationsHistoryTable, TenancyDbContext.Schema))
            .Options;
        return new TenancyDbContext(options, new TenantContext());
    }
}
