using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Crm.Contracts;
using Crm.Kernel.Tenancy;
using Crm.Profiles.Infrastructure;
using Crm.Tenancy.Infrastructure;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.Kafka;
using Testcontainers.Redis;
using Xunit;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>
/// One SQL Server (image from MSSQL_IMAGE, default SQL Server 2022), one Kafka, one Redis for the whole test run.
/// Works on Docker and on Podman (DOCKER_HOST + TESTCONTAINERS_RYUK_DISABLED=true, see docs/dev-setup.md).
/// </summary>
public sealed class CrmStackFixture : IAsyncLifetime
{
    public const string SaPassword = "Crm_Test_Passw0rd!";

    private readonly IContainer _sql = new ContainerBuilder(Environment.GetEnvironmentVariable("MSSQL_IMAGE") ?? "mcr.microsoft.com/mssql/server:2022-latest")
        .WithEnvironment("ACCEPT_EULA", "Y")
        .WithEnvironment("MSSQL_SA_PASSWORD", SaPassword)
        .WithEnvironment("MSSQL_PID", "Developer")
        .WithPortBinding(1433, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(1433))
        .Build();

    // Same images as deploy/docker-compose.yml; tools/tests/test_image_pins.py fails when they drift.
    private readonly KafkaContainer _kafka = new KafkaBuilder("apache/kafka:3.9.1").Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    public string SqlConnectionString { get; private set; } = string.Empty;

    public string RedisConfiguration { get; private set; } = string.Empty;

    public string KafkaBootstrapServers { get; private set; } = string.Empty;

    public SwitchableHooks Hooks { get; } = new();

    public CrmWorkerFactory Worker { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_sql.StartAsync(), _kafka.StartAsync(), _redis.StartAsync());

        SqlConnectionString = $"Server={_sql.Hostname},{_sql.GetMappedPublicPort(1433)};Database=crm_test;User Id=sa;Password={SaPassword};TrustServerCertificate=True;Encrypt=False";
        RedisConfiguration = _redis.GetConnectionString();
        KafkaBootstrapServers = _kafka.GetBootstrapAddress();

        await WaitForSqlLoginAsync(TimeSpan.FromSeconds(90));
        await EnsureDatabaseAsync("crm_test");
        await MigrateTenancyAsync();
        await MigrateProfilesAsync();
        await CreateTopicsAsync();

        Worker = new CrmWorkerFactory(this, Hooks);
        Worker.Start();
    }

    /// <summary>compose's kafka-init creates these with 12 partitions and auto-creation off; mirror it so keys spread the same way.</summary>
    private async Task CreateTopicsAsync()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = KafkaBootstrapServers }).Build();
        try
        {
            await admin.CreateTopicsAsync(new[] { KafkaTopics.EventsRaw, KafkaTopics.EventsProcessed, KafkaTopics.EventsDlq }
                .Select(name => new TopicSpecification { Name = name, NumPartitions = KafkaTopics.Partitions, ReplicationFactor = 1 }));
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
        {
            // created by an earlier fixture instance or by broker auto-creation
        }
    }

    public Task PauseKafkaAsync() => ((IContainer)_kafka).PauseAsync();

    public Task UnpauseKafkaAsync() => ((IContainer)_kafka).UnpauseAsync();

    /// <summary>The API only auto-migrates in Development; tests run as "Testing", so the fixture creates the database itself.</summary>
    private async Task EnsureDatabaseAsync(string name)
    {
        var master = new SqlConnectionStringBuilder(SqlConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"IF DB_ID(N'{name}') IS NULL CREATE DATABASE [{name}];";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Tests run as "Testing", where the API does not auto-migrate; the fixture owns the schema.</summary>
    private async Task MigrateTenancyAsync()
    {
        var options = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseSqlServer(SqlConnectionString, sql => sql.MigrationsHistoryTable(TenancyDbContext.MigrationsHistoryTable, TenancyDbContext.Schema))
            .Options;
        await using var db = new TenancyDbContext(options, new TenantContext());
        await db.Database.MigrateAsync();
    }

    private async Task MigrateProfilesAsync()
    {
        var options = new DbContextOptionsBuilder<ProfilesDbContext>()
            .UseSqlServer(SqlConnectionString, sql => sql.MigrationsHistoryTable(ProfilesDbContext.MigrationsHistoryTable, ProfilesDbContext.Schema))
            .Options;
        await using var db = new ProfilesDbContext(options, new TenantContext());
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (Worker is not null)
        {
            await Worker.DisposeAsync();
        }

        await Task.WhenAll(_sql.DisposeAsync().AsTask(), _kafka.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }

    public Task PauseRedisAsync() => ((IContainer)_redis).PauseAsync();

    public Task UnpauseRedisAsync() => ((IContainer)_redis).UnpauseAsync();

    private async Task WaitForSqlLoginAsync(TimeSpan budget)
    {
        var master = new SqlConnectionStringBuilder(SqlConnectionString) { InitialCatalog = "master" }.ConnectionString;
        var deadline = DateTime.UtcNow + budget;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var connection = new SqlConnection(master);
                await connection.OpenAsync();
                return;
            }
            catch (SqlException ex)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        throw new TimeoutException($"SQL Server did not accept logins within {budget}.", last);
    }
}
