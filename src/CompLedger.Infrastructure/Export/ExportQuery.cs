using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using CompLedger.Infrastructure.Db;
using CompLedger.Infrastructure.Queries;
using Dapper;
using Npgsql;

namespace CompLedger.Infrastructure.Export;

/// <summary>One row of the export: a value and the period it is in effect. <see cref="EffectiveTo"/> is inclusive; null means open-ended.</summary>
public readonly record struct ExportRow(string EmployeeId, string Component, DateOnly EffectiveFrom, DateOnly? EffectiveTo, decimal Amount, string? Currency);

/// <summary>
/// The export (PRP-004 3.4, 7.2) in two transactions: the first waits for a batch of the tenant that is being applied
/// and reads the clock, then commits and so releases the shared lock; the second streams the rows known at the
/// instant, without the lock, so a batch of the tenant can apply while the file is still being sent.
/// </summary>
public sealed class ExportQuery(NpgsqlDataSource dataSource)
{
    /// <summary>The streaming statement runs for as long as the file takes to read; a slow client is not a failure.</summary>
    private const int StreamTimeoutSeconds = 600;

    /// <summary>
    /// The PRP-002 as-known-at as-of read for every employee at once. The known-at filter runs before the window
    /// function, so a value outside the known set never splits a period. Ordinal order on both keys (6.5 #2).
    /// </summary>
    private const string RowsSql =
        $"""
        /* comp-ledger export rows */
        WITH periods AS (
          SELECT employee_id, component, amount, currency::text AS currency, effective_from,
                 lead(effective_from) OVER (PARTITION BY employee_id, component ORDER BY effective_from) - 1 AS effective_to
          FROM compensation_values
          WHERE tenant_id = @TenantId AND {CompensationQueries.KnownAtInstant}
        )
        SELECT employee_id, component, effective_from, effective_to, amount, currency
        FROM periods
        WHERE effective_from <= @AsOf::date AND (effective_to IS NULL OR effective_to >= @AsOf::date)
        ORDER BY employee_id COLLATE "C", component COLLATE "C"
        """;

    /// <summary>
    /// Transaction 1: waits while a batch of the tenant is being applied, reads the request time on the database
    /// clock and commits, which ends the shared lock. After it, no batch can get an apply time at or before the
    /// returned instant (PRP-002 3.4 item 4).
    /// </summary>
    public async Task<DateTime> FixInstantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = await TenantScope.BeginAsync(dataSource, tenantId, cancellationToken);
        var requestTime = await scope.WaitForAppliesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return requestTime;
    }

    /// <summary>
    /// Transaction 2: the rows in effect on <paramref name="asOf"/> among the values known at <paramref name="knownAt"/>,
    /// in file order, read one at a time from a single statement. Nothing is collected: each row is yielded as it is
    /// read. Call only with an instant fixed by <see cref="FixInstantAsync"/> or a snapshot.
    /// </summary>
    public async IAsyncEnumerable<ExportRow> ReadAsync(
        Guid tenantId, DateOnly asOf, DateTime knownAt, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var scope = await TenantScope.BeginAsync(dataSource, tenantId, cancellationToken);
        await using (var reader = await scope.Connection.ExecuteReaderAsync(
                         new CommandDefinition(
                             RowsSql,
                             new { TenantId = tenantId, KnownAt = knownAt, AsOf = asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                             scope.Transaction,
                             StreamTimeoutSeconds,
                             cancellationToken: cancellationToken),
                         CommandBehavior.SequentialAccess))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                // Sequential access: the columns are read in order, each once.
                var employeeId = await reader.GetFieldValueAsync<string>(0, cancellationToken);
                var component = await reader.GetFieldValueAsync<string>(1, cancellationToken);
                var effectiveFrom = await reader.GetFieldValueAsync<DateOnly>(2, cancellationToken);
                DateOnly? effectiveTo = await reader.IsDBNullAsync(3, cancellationToken)
                    ? null
                    : await reader.GetFieldValueAsync<DateOnly>(3, cancellationToken);
                var amount = await reader.GetFieldValueAsync<decimal>(4, cancellationToken);
                var currency = await reader.IsDBNullAsync(5, cancellationToken)
                    ? null
                    : await reader.GetFieldValueAsync<string>(5, cancellationToken);
                yield return new ExportRow(employeeId, component, effectiveFrom, effectiveTo, amount, currency);
            }
        }

        await scope.CommitAsync(cancellationToken);
    }
}
