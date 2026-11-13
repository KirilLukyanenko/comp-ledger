using System.Security.Cryptography;
using CompLedger.Infrastructure.Db;
using Dapper;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(CompLedger.IntegrationTests.PostgresFixture))]

namespace CompLedger.IntegrationTests;

/// <summary>
/// One PostgreSQL 17 container for the whole test assembly. It plays the environment's part from PRP-001 6.4:
/// creates <c>comp_app</c> and a LOGIN role in it with a random password, then runs the migrations as the owner.
/// Tests reach the database only through the non-superuser app login; each test creates its own tenant.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string AppLogin = "comp_app_login";

    /// <summary>The database's host name on <see cref="Network"/>, for other containers.</summary>
    public const string NetworkAlias = "db";

    private readonly PostgreSqlContainer _container;

    private NpgsqlDataSource? _appDataSource;

    public PostgresFixture()
    {
        Network = new NetworkBuilder().Build();
        _container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithPassword(RandomPassword())
            .WithNetwork(Network)
            .WithNetworkAliases(NetworkAlias)
            .Build();
    }

    /// <summary>A network shared with containers that must reach the database, such as the API image under test.</summary>
    public INetwork Network { get; }

    /// <summary>Connects as the container's superuser, which owns the schema. Only for migrations.</summary>
    public string OwnerConnectionString { get; private set; } = "";

    /// <summary>Connects as the app login: member of <c>comp_app</c>, no superuser, no BYPASSRLS.</summary>
    public string AppConnectionString { get; private set; } = "";

    public NpgsqlDataSource AppDataSource =>
        _appDataSource ?? throw new InvalidOperationException("The fixture is not initialized.");

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        OwnerConnectionString = _container.GetConnectionString();

        var appPassword = RandomPassword();
        await using (var owner = new NpgsqlConnection(OwnerConnectionString))
        {
            await owner.OpenAsync();
            // The password is random hex, so it is safe to inline; CREATE ROLE takes no parameters.
            await owner.ExecuteAsync(
                $"CREATE ROLE comp_app NOLOGIN; CREATE ROLE {AppLogin} LOGIN PASSWORD '{appPassword}' IN ROLE comp_app;");
        }

        Migrator.Migrate(OwnerConnectionString);

        AppConnectionString = new NpgsqlConnectionStringBuilder(OwnerConnectionString)
        {
            Username = AppLogin,
            Password = appPassword,
        }.ConnectionString;
        _appDataSource = NpgsqlDataSource.Create(AppConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (_appDataSource is not null)
        {
            await _appDataSource.DisposeAsync();
        }

        await _container.DisposeAsync();
        await Network.DisposeAsync();
    }

    /// <summary>
    /// Creates an empty database on the same server and returns the owner and app connection strings that reach it
    /// from another container on <see cref="Network"/>. Roles are server-wide, so the app login already exists.
    /// </summary>
    public async Task<(string Owner, string App)> CreateEmptyDatabaseForContainersAsync(string name)
    {
        await using (var owner = new NpgsqlConnection(OwnerConnectionString))
        {
            await owner.OpenAsync();
            await owner.ExecuteAsync($"CREATE DATABASE {name}");
        }

        string ForContainer(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString)
        {
            Host = NetworkAlias,
            Port = PostgreSqlBuilder.PostgreSqlPort,
            Database = name,
        }.ConnectionString;

        return (ForContainer(OwnerConnectionString), ForContainer(AppConnectionString));
    }

    /// <summary>Inserts a tenant through the app login and returns its id.</summary>
    public async Task<Guid> CreateTenantAsync(string name = "Test tenant")
    {
        var id = Guid.CreateVersion7();
        await using var connection = await AppDataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("INSERT INTO tenants (id, name) VALUES (@id, @name)", new { id, name });
        return id;
    }

    private static string RandomPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
}
