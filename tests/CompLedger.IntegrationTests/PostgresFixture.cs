using System.Security.Cryptography;
using CompLedger.Infrastructure.Db;
using Dapper;
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

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithPassword(RandomPassword())
        .Build();

    private NpgsqlDataSource? _appDataSource;

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
