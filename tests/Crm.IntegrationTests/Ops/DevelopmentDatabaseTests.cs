using Crm.Api;
using Crm.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Crm.IntegrationTests.Ops;

/// <summary>Task 9: <see cref="DevelopmentDatabase"/> must not lose a race against a Worker-like session (ruling F18).</summary>
[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class DevelopmentDatabaseTests(CrmStackFixture stack)
{
    /// <summary>
    /// The brief's intruder (a pooled connection opened, queried and disposed every 50 ms) never wins the
    /// single-digit-millisecond window between CREATE DATABASE and the ALTER that follows it on this stack: every
    /// attempt, success or failure, pays a real network round trip to the remote engine, which is already slower
    /// than the window it is trying to land in. Measured RED evidence (below) shows zero successful connections
    /// across twelve such loops for the whole 1 s <c>EnsureCreatedAsync</c> call. This intruder instead polls
    /// <c>DB_ID()</c> from inside a single T-SQL batch with <c>WAITFOR DELAY</c> — a loop that runs entirely on the
    /// server, with no network round trip per check — and switches that same session's database context with
    /// <c>USE</c> the instant the loop condition clears, which is the earliest a session can possibly become
    /// "connected to" the new database. Several of these run concurrently for reliability. Deviation from the
    /// brief's design, noted in the report; it still holds a real sleeping session in the target database exactly
    /// as the brief requires, which is what <c>WITH ROLLBACK IMMEDIATE</c> must be able to disconnect.
    /// </summary>
    private const int IntruderCount = 5;

    [Fact]
    public async Task FR24_database_is_created_with_rcsi_even_while_another_session_keeps_connecting()
    {
        var databaseName = $"crm_race_{Guid.NewGuid():N}";
        var connectionString = new SqlConnectionStringBuilder(stack.SqlConnectionString) { InitialCatalog = databaseName }.ConnectionString;
        var logger = new ListLogger();

        using var intruderCts = new CancellationTokenSource();
        var intruderTasks = Enumerable.Range(0, IntruderCount)
            .Select(_ => RunIntruderAsync(connectionString, databaseName, intruderCts.Token))
            .ToArray();

        try
        {
            await DevelopmentDatabase.EnsureCreatedAsync(connectionString, logger, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));

            var isOn = await GetIsRcsiOnAsync(databaseName);
            isOn.Should().BeTrue();
        }
        finally
        {
            await intruderCts.CancelAsync();
            await Task.WhenAll(intruderTasks);
            SqlConnection.ClearAllPools();
            await DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task FR24_existing_database_is_left_alone()
    {
        var databaseName = $"crm_race_{Guid.NewGuid():N}";
        var connectionString = new SqlConnectionStringBuilder(stack.SqlConnectionString) { InitialCatalog = databaseName }.ConnectionString;
        var logger = new ListLogger();

        try
        {
            await DevelopmentDatabase.EnsureCreatedAsync(connectionString, logger, CancellationToken.None);
            logger.Messages.Clear();

            await DevelopmentDatabase.EnsureCreatedAsync(connectionString, logger, CancellationToken.None);

            logger.Messages.Should().NotContain(m => m.Contains("created", StringComparison.OrdinalIgnoreCase));
            logger.Messages.Should().NotContain(m => m.Contains("switched", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await DropDatabaseAsync(databaseName);
        }
    }

    /// <summary>
    /// Connects to master, then in one batch waits (server-side, via <c>WAITFOR DELAY</c>, no network round trips)
    /// until the target database exists and immediately <c>USE</c>s it — becoming a session "connected to" that
    /// database the instant it is possible to be one. Holds that session briefly, then goes back to polling, in
    /// case an earlier lap missed the (one-time) window: the whole point is to have at least one lap sitting in
    /// the database precisely when <see cref="DevelopmentDatabase"/> tries to ALTER it.
    /// </summary>
    private static async Task RunIntruderAsync(string connectionString, string databaseName, CancellationToken cancellationToken)
    {
        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;
        var quotedName = new SqlCommandBuilder().QuoteIdentifier(databaseName);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new SqlConnection(master);
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandTimeout = 40;
#pragma warning disable CA2100 // databaseName is a test-generated identifier, bracket-quoted via QuoteIdentifier
                command.CommandText = $"""
                    WHILE DB_ID(N'{databaseName}') IS NULL
                    BEGIN
                        WAITFOR DELAY '00:00:00.002';
                    END
                    USE {quotedName};
                    """;
#pragma warning restore CA2100
                await command.ExecuteNonQueryAsync(cancellationToken);

                // Now sleeping in the target database; hold it a moment so a slow ALTER can see it.
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }
            catch (SqlException)
            {
                // e.g. this session was the one disconnected by WITH ROLLBACK IMMEDIATE; retry.
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<bool> GetIsRcsiOnAsync(string databaseName)
    {
        var master = new SqlConnectionStringBuilder(stack.SqlConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = @name;";
        command.Parameters.AddWithValue("@name", databaseName);
        return (bool?)await command.ExecuteScalarAsync() ?? false;
    }

    private async Task DropDatabaseAsync(string databaseName)
    {
        var master = new SqlConnectionStringBuilder(stack.SqlConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        var quotedName = new SqlCommandBuilder().QuoteIdentifier(databaseName);
#pragma warning disable CA2100 // databaseName is a test-generated identifier, bracket-quoted via QuoteIdentifier
        command.CommandText = $"IF DB_ID(N'{databaseName}') IS NOT NULL BEGIN ALTER DATABASE {quotedName} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {quotedName}; END";
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Messages.Add(formatter(state, exception));
        }
    }
}
