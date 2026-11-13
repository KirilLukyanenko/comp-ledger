using System.Globalization;
using CompLedger.Infrastructure.Db;
using Dapper;
using Npgsql;

namespace CompLedger.Infrastructure.Snapshots;

/// <summary>A named pair of a date and an instant (PRP-002 3.1). <see cref="KnownAt"/> is UTC.</summary>
public sealed record Snapshot(Guid SnapshotId, string Name, DateOnly AsOf, DateTime KnownAt);

/// <summary>The <c>snapshots</c> table, inside a tenant scope. Snapshots are created and read, never changed.</summary>
public sealed class SnapshotStore(NpgsqlDataSource dataSource)
{
    private const string NameIndex = "ux_snapshots_tenant_name";

    private const string Select =
        """
        SELECT id AS SnapshotId, name AS Name, as_of AS AsOf, known_at AS KnownAt
        FROM snapshots

        """;

    /// <summary>
    /// Stores a snapshot whose instant is the database clock, read after the wait for a batch of the tenant that is
    /// being applied (PRP-002 3.4 items 4 and 5). Constant time: no values are read or copied.
    /// Returns null when the tenant already has a snapshot with this name.
    /// </summary>
    public async Task<Snapshot?> CreateAsync(Guid tenantId, string name, DateOnly asOf, CancellationToken cancellationToken)
    {
        var snapshotId = Guid.CreateVersion7();
        await using var scope = await TenantScope.BeginAsync(dataSource, tenantId, cancellationToken);
        var knownAt = await scope.WaitForAppliesAsync(cancellationToken);

        try
        {
            await scope.Connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO snapshots (id, tenant_id, name, as_of, known_at) VALUES (@SnapshotId, @TenantId, @Name, @AsOf::date, @KnownAt)",
                new
                {
                    SnapshotId = snapshotId,
                    TenantId = tenantId,
                    Name = name,
                    AsOf = asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    KnownAt = knownAt,
                },
                scope.Transaction,
                cancellationToken: cancellationToken));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == NameIndex)
        {
            // The unique index decides, so two concurrent creations with one name cannot both succeed.
            return null;
        }

        await scope.CommitAsync(cancellationToken);
        return new Snapshot(snapshotId, name, asOf, knownAt);
    }

    /// <summary>Returns the snapshot, or null when it does not exist in this tenant.</summary>
    public async Task<Snapshot?> FindAsync(Guid tenantId, Guid snapshotId, CancellationToken cancellationToken)
    {
        await using var scope = await TenantScope.BeginAsync(dataSource, tenantId, cancellationToken);
        var row = await scope.Connection.QuerySingleOrDefaultAsync<SnapshotRow>(new CommandDefinition(
            Select + "WHERE id = @SnapshotId AND tenant_id = @TenantId",
            new { SnapshotId = snapshotId, TenantId = tenantId },
            scope.Transaction,
            cancellationToken: cancellationToken));
        await scope.CommitAsync(cancellationToken);
        return row is null ? null : ToSnapshot(row);
    }

    /// <summary>The tenant's snapshots, ordered by instant, then id.</summary>
    public async Task<IReadOnlyList<Snapshot>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = await TenantScope.BeginAsync(dataSource, tenantId, cancellationToken);
        var rows = (await scope.Connection.QueryAsync<SnapshotRow>(new CommandDefinition(
            Select + "WHERE tenant_id = @TenantId ORDER BY known_at, id",
            new { TenantId = tenantId },
            scope.Transaction,
            cancellationToken: cancellationToken))).AsList();
        await scope.CommitAsync(cancellationToken);
        return rows.ConvertAll(ToSnapshot);
    }

    private static Snapshot ToSnapshot(SnapshotRow row) =>
        new(row.SnapshotId, row.Name, DateOnly.FromDateTime(row.AsOf), DateTime.SpecifyKind(row.KnownAt, DateTimeKind.Utc));

    private sealed class SnapshotRow
    {
        public Guid SnapshotId { get; set; }

        public string Name { get; set; } = "";

        public DateTime AsOf { get; set; }

        public DateTime KnownAt { get; set; }
    }
}
