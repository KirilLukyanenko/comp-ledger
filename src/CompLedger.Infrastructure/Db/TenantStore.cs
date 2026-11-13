using Dapper;
using Npgsql;

namespace CompLedger.Infrastructure.Db;

/// <summary>The <c>tenants</c> table. It is not tenant-owned, so it is used without a tenant scope.</summary>
public sealed class TenantStore(NpgsqlDataSource dataSource)
{
    public async Task<bool> ExistsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM tenants WHERE id = @TenantId)", new { TenantId = tenantId }, cancellationToken: cancellationToken));
    }

    public async Task<Guid> CreateAsync(string name, CancellationToken cancellationToken)
    {
        var tenantId = Guid.CreateVersion7();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO tenants (id, name) VALUES (@TenantId, @Name)", new { TenantId = tenantId, Name = name }, cancellationToken: cancellationToken));
        return tenantId;
    }
}
