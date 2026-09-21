using Microsoft.Data.SqlClient;

namespace Crm.Api;

/// <summary>
/// In Development, ensures the database named in a connection string's <c>Initial Catalog</c> exists — with
/// READ_COMMITTED_SNAPSHOT already on — before <c>Program</c> runs its first EF Core migration. Without this,
/// EF Core's own database creator issues an unguarded <c>ALTER DATABASE … SET READ_COMMITTED_SNAPSHOT ON</c>
/// right after <c>CREATE DATABASE</c>; the Worker starts in parallel by design and opens pooled connections to
/// the database as soon as it exists, and whoever slips in between those two statements blocks the ALTER until
/// its command timeout, crashing the host (ruling F18, <c>docs/dev-setup.md</c>).
/// </summary>
internal static class DevelopmentDatabase
{
    private const int CommandTimeoutSeconds = 60;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Creates the database if it does not exist and switches READ_COMMITTED_SNAPSHOT on if it is not already on.
    /// A database that already exists with the option on costs two cheap catalog reads. Retries the whole
    /// sequence once on <see cref="SqlException"/>, for a transient connection error while SQL Server is still
    /// warming up — a session reconnecting between the CREATE and the ALTER is harmless, the ALTER handles it.
    /// </summary>
    public static async Task EnsureCreatedAsync(string connectionString, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            await EnsureCreatedOnceAsync(connectionString, logger, cancellationToken);
        }
        catch (SqlException)
        {
            await Task.Delay(RetryDelay, cancellationToken);
            await EnsureCreatedOnceAsync(connectionString, logger, cancellationToken);
        }
    }

    private static async Task EnsureCreatedOnceAsync(string connectionString, ILogger logger, CancellationToken cancellationToken)
    {
        var target = new SqlConnectionStringBuilder(connectionString);
        var databaseName = target.InitialCatalog;

        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", Pooling = false };
        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        // A database name cannot be a parameter of CREATE/ALTER DATABASE. QuoteIdentifier brackets it the same
        // way QUOTENAME would; the name comes from configuration (ConnectionStrings:Crm), never from a request.
        var quotedName = new SqlCommandBuilder().QuoteIdentifier(databaseName);

        await using (var existsCommand = connection.CreateCommand())
        {
            existsCommand.CommandTimeout = CommandTimeoutSeconds;
            existsCommand.CommandText = "SELECT DB_ID(@name);";
            existsCommand.Parameters.AddWithValue("@name", databaseName);
            var id = await existsCommand.ExecuteScalarAsync(cancellationToken);
            if (id is null or DBNull)
            {
                await using var createCommand = connection.CreateCommand();
                createCommand.CommandTimeout = CommandTimeoutSeconds;
#pragma warning disable CA2100 // databaseName is bracket-quoted via QuoteIdentifier and comes from configuration, not a request
                createCommand.CommandText = $"CREATE DATABASE {quotedName};";
#pragma warning restore CA2100
                await createCommand.ExecuteNonQueryAsync(cancellationToken);
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Created Development database {DatabaseName}", databaseName);
                }
            }
        }

        await using (var rcsiCommand = connection.CreateCommand())
        {
            rcsiCommand.CommandTimeout = CommandTimeoutSeconds;
            rcsiCommand.CommandText = "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = @name;";
            rcsiCommand.Parameters.AddWithValue("@name", databaseName);
            var isOn = (bool?)await rcsiCommand.ExecuteScalarAsync(cancellationToken) ?? false;
            if (!isOn)
            {
                await using var alterCommand = connection.CreateCommand();
                alterCommand.CommandTimeout = CommandTimeoutSeconds;
#pragma warning disable CA2100 // databaseName is bracket-quoted via QuoteIdentifier and comes from configuration, not a request
                alterCommand.CommandText = $"ALTER DATABASE {quotedName} SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;";
#pragma warning restore CA2100
                await alterCommand.ExecuteNonQueryAsync(cancellationToken);
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Switched READ_COMMITTED_SNAPSHOT on for Development database {DatabaseName}", databaseName);
                }
            }
        }
    }
}
