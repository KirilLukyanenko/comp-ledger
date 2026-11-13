using Dapper;
using Npgsql;

namespace CompLedger.Infrastructure.Db;

/// <summary>
/// A connection with an open transaction whose <c>app.tenant_id</c> is set transaction-locally, so row-level security
/// sees the tenant and nothing leaks through the connection pool. Disposing without a commit rolls back.
/// </summary>
public sealed class TenantScope : IAsyncDisposable
{
    /// <summary>Applying a large batch can take longer than Npgsql's default command timeout.</summary>
    private const int LockTimeoutSeconds = 600;

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

    /// <summary>
    /// Waits while a batch of this tenant is being applied, then returns the database clock (PRP-002 3.4 item 4).
    /// Ingestion holds the exclusive form of this lock from its apply time to its commit, so after the wait every
    /// batch applied at or before the returned instant has committed, and every later batch gets a later apply time.
    /// The lock is transaction-level and shared: readers do not wait for each other, and it ends with the transaction.
    /// </summary>
    public async Task<DateTime> WaitForAppliesAsync(CancellationToken cancellationToken)
    {
        await Connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock_shared(hashtextextended(@Tenant, 0))",
            new { Tenant = _tenantId.ToString("D") },
            Transaction,
            LockTimeoutSeconds,
            cancellationToken: cancellationToken));

        // A statement of its own, after the lock: in READ COMMITTED each statement sees what was committed before it
        // started, and so does every statement the caller runs after this one.
        var requestTime = await Connection.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "SELECT clock_timestamp()", transaction: Transaction, cancellationToken: cancellationToken));
        return DateTime.SpecifyKind(requestTime, DateTimeKind.Utc);
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
