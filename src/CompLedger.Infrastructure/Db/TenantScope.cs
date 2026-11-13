using Dapper;
using Npgsql;

namespace CompLedger.Infrastructure.Db;

/// <summary>
/// A connection with an open transaction whose <c>app.tenant_id</c> is set transaction-locally, so row-level security
/// sees the tenant and nothing leaks through the connection pool. Disposing without a commit rolls back.
/// </summary>
public sealed class TenantScope : IAsyncDisposable
{
    private readonly Guid _tenantId;
    private NpgsqlTransaction? _transaction;

    private TenantScope(NpgsqlConnection connection, Guid tenantId)
    {
        Connection = connection;
        _tenantId = tenantId;
    }

    public NpgsqlConnection Connection { get; }

    public NpgsqlTransaction Transaction =>
        _transaction ?? throw new InvalidOperationException("The tenant scope has no open transaction.");

    public static async Task<TenantScope> BeginAsync(NpgsqlDataSource dataSource, Guid tenantId, CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var scope = new TenantScope(connection, tenantId);
            await scope.BeginTransactionAsync(cancellationToken);
            return scope;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        await Transaction.CommitAsync(cancellationToken);
        await DisposeTransactionAsync();
    }

    /// <summary>Rolls back the current transaction and opens a new one for the same tenant on the same connection.</summary>
    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await Transaction.RollbackAsync(cancellationToken);
        await DisposeTransactionAsync();
        await BeginTransactionAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        // Disposing an uncommitted Npgsql transaction rolls it back.
        await DisposeTransactionAsync();
        await Connection.DisposeAsync();
    }

    private async Task BeginTransactionAsync(CancellationToken cancellationToken)
    {
        _transaction = await Connection.BeginTransactionAsync(cancellationToken);
        await Connection.ExecuteAsync(new CommandDefinition(
            "SELECT set_config('app.tenant_id', @TenantId, true)",
            new { TenantId = _tenantId.ToString("D") },
            _transaction,
            cancellationToken: cancellationToken));
    }

    private async Task DisposeTransactionAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync();
            _transaction = null;
        }
    }
}
