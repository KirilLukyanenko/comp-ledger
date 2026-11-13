using System.Globalization;
using System.Text;
using System.Text.Json;
using CompLedger.Core.Ingestion;
using CompLedger.Infrastructure.Db;
using CsvHelper;
using CsvHelper.Configuration;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace CompLedger.Infrastructure.Ingestion;

/// <summary>
/// Applies one CSV upload as one atomic batch (PRP-001 3.5 and 7.2): the body is validated while it streams into a
/// temporary staging table through binary COPY, then merged into <c>compensation_values</c> with set-based SQL.
/// </summary>
public sealed class PostgresIngestor(NpgsqlDataSource dataSource)
{
    /// <summary>The merge of a large file can take longer than Npgsql's default command timeout.</summary>
    private const int CommandTimeoutSeconds = 600;

    private static readonly JsonSerializerOptions ErrorJsonOptions = new(JsonSerializerDefaults.Web);

    // Bad bytes throw instead of becoming U+FFFD; the preamble makes the reader skip a UTF-8 BOM.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);

    public async Task<IngestionBatch> IngestAsync(Guid tenantId, Stream body, CancellationToken cancellationToken)
    {
        var batchId = Guid.CreateVersion7();
        var receivedAt = UtcNow();
        var errors = new ErrorCollector();
        var parser = new RowParser(errors);

        await using var scope = await TenantScope.BeginAsync(dataSource, tenantId, cancellationToken);

        await ExecuteAsync(
            scope,
            """
            CREATE TEMP TABLE staging (row_no int, employee_id text, component text,
                                       effective_from date, amount numeric(19,4), currency char(3)) ON COMMIT DROP
            """,
            null,
            cancellationToken);
        await StageAsync(scope.Connection, body, parser, cancellationToken);
        await ExecuteAsync(scope, "ANALYZE staging", null, cancellationToken);
        await AddDuplicateKeyErrorsAsync(scope, errors, cancellationToken);

        if (errors.HasErrors)
        {
            // Drop everything staged, then record the rejection in a transaction of its own.
            await scope.RestartAsync(cancellationToken);
            var rejected = new IngestionBatch(
                batchId, BatchStatus.Rejected, receivedAt, UtcNow(), new BatchRows(parser.TotalRows, 0, 0, 0), errors.Count, errors.ToList());
            await InsertBatchAsync(scope, tenantId, rejected, cancellationToken);
            await scope.CommitAsync(cancellationToken);
            return rejected;
        }

        // From here on, batches of one tenant run one at a time. The lock is taken only now, after the body was read.
        await ExecuteAsync(
            scope, "SELECT pg_advisory_xact_lock(hashtextextended(@Tenant, 0))", new { Tenant = tenantId.ToString("D") }, cancellationToken);

        // One apply time per batch, read after the lock. now() would be the transaction start, which is before it.
        var appliedAt = await scope.Connection.ExecuteScalarAsync<DateTime>(
            new CommandDefinition("SELECT clock_timestamp()", transaction: scope.Transaction, cancellationToken: cancellationToken));

        var total = parser.TotalRows;
        await InsertBatchAsync(
            scope,
            tenantId,
            new IngestionBatch(batchId, BatchStatus.Applied, receivedAt, receivedAt, new BatchRows(total, 0, 0, 0), 0, []),
            cancellationToken);

        var mergeParameters = new { TenantId = tenantId, BatchId = batchId, AppliedAt = appliedAt };

        // Corrected: supersede the current rows whose value differs. Amounts compare numerically.
        var corrected = await ExecuteAsync(
            scope,
            """
            UPDATE compensation_values c
            SET superseded_at = @AppliedAt, superseded_by_batch_id = @BatchId
            FROM staging s
            WHERE c.tenant_id = @TenantId AND c.superseded_at IS NULL
              AND c.employee_id = s.employee_id AND c.component = s.component AND c.effective_from = s.effective_from
              AND (c.amount <> s.amount OR c.currency IS DISTINCT FROM s.currency)
            """,
            mergeParameters,
            cancellationToken);

        // Insert every staged row that now has no current value: the new keys and the corrected ones.
        var written = await ExecuteAsync(
            scope,
            """
            INSERT INTO compensation_values (tenant_id, employee_id, component, effective_from, amount, currency, batch_id, recorded_at)
            SELECT @TenantId, s.employee_id, s.component, s.effective_from, s.amount, s.currency, @BatchId, @AppliedAt
            FROM staging s
            WHERE NOT EXISTS (
              SELECT 1 FROM compensation_values c
              WHERE c.tenant_id = @TenantId AND c.superseded_at IS NULL
                AND c.employee_id = s.employee_id AND c.component = s.component AND c.effective_from = s.effective_from)
            """,
            mergeParameters,
            cancellationToken);

        var applied = new IngestionBatch(
            batchId, BatchStatus.Applied, receivedAt, UtcNow(), new BatchRows(total, written - corrected, corrected, total - written), 0, []);
        await ExecuteAsync(
            scope,
            """
            UPDATE ingestion_batches
            SET inserted_rows = @Inserted, corrected_rows = @Corrected, unchanged_rows = @Unchanged, completed_at = @CompletedAt
            WHERE id = @BatchId AND tenant_id = @TenantId
            """,
            new
            {
                applied.Rows.Inserted,
                applied.Rows.Corrected,
                applied.Rows.Unchanged,
                applied.CompletedAt,
                BatchId = batchId,
                TenantId = tenantId,
            },
            cancellationToken);

        await scope.CommitAsync(cancellationToken);
        return applied;
    }

    /// <summary>Returns the batch, or null when it does not exist in this tenant.</summary>
    public async Task<IngestionBatch?> FindBatchAsync(Guid tenantId, Guid batchId, CancellationToken cancellationToken)
    {
        await using var scope = await TenantScope.BeginAsync(dataSource, tenantId, cancellationToken);
        var record = await scope.Connection.QuerySingleOrDefaultAsync<BatchRecord>(new CommandDefinition(
            """
            SELECT id AS BatchId, status AS Status, received_at AS ReceivedAt, completed_at AS CompletedAt,
                   total_rows AS Total, inserted_rows AS Inserted, corrected_rows AS Corrected, unchanged_rows AS Unchanged,
                   error_count AS ErrorCount, errors::text AS ErrorsJson
            FROM ingestion_batches
            WHERE id = @BatchId AND tenant_id = @TenantId
            """,
            new { BatchId = batchId, TenantId = tenantId },
            scope.Transaction,
            cancellationToken: cancellationToken));
        await scope.CommitAsync(cancellationToken);

        if (record is null)
        {
            return null;
        }

        return new IngestionBatch(
            record.BatchId,
            record.Status,
            DateTime.SpecifyKind(record.ReceivedAt, DateTimeKind.Utc),
            DateTime.SpecifyKind(record.CompletedAt, DateTimeKind.Utc),
            new BatchRows(record.Total, record.Inserted, record.Corrected, record.Unchanged),
            record.ErrorCount,
            JsonSerializer.Deserialize<List<IngestionError>>(record.ErrorsJson, ErrorJsonOptions) ?? []);
    }

    /// <summary>
    /// Reads the body record by record, validates each one and copies the valid rows into <c>staging</c>.
    /// Only one record is in memory at a time.
    /// </summary>
    private static async Task StageAsync(NpgsqlConnection connection, Stream body, RowParser parser, CancellationToken cancellationToken)
    {
        await using var importer = await connection.BeginBinaryImportAsync(
            "COPY staging (row_no, employee_id, component, effective_from, amount, currency) FROM STDIN (FORMAT BINARY)",
            cancellationToken);

        using var reader = new StreamReader(body, StrictUtf8, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);

        var badData = false;
        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = ",",
            Mode = CsvMode.RFC4180,
            DetectDelimiter = false,
            AllowComments = false,
            TrimOptions = TrimOptions.None,

            // Blank lines are records here: RowParser decides whether one is an error or ignorable trailing space.
            IgnoreBlankLines = false,
            BadDataFound = _ => badData = true,
        };
        using var csv = new CsvParser(reader, configuration, leaveOpen: true);

        try
        {
            while (!parser.HeaderFailed)
            {
                badData = false;
                if (!await csv.ReadAsync())
                {
                    parser.Complete();
                    break;
                }

                // Fields are unquoted lazily; reading the record is what reports bad data.
                var record = csv.Record ?? [];
                if (parser.Accept(record, badData, out var row))
                {
                    await importer.StartRowAsync(cancellationToken);
                    await importer.WriteAsync(row.RowNumber, NpgsqlDbType.Integer, cancellationToken);
                    await importer.WriteAsync(row.EmployeeId, NpgsqlDbType.Text, cancellationToken);
                    await importer.WriteAsync(row.Component, NpgsqlDbType.Text, cancellationToken);
                    await importer.WriteAsync(row.EffectiveFrom, NpgsqlDbType.Date, cancellationToken);
                    await importer.WriteAsync(row.Amount, NpgsqlDbType.Numeric, cancellationToken);
                    if (row.Currency is null)
                    {
                        await importer.WriteNullAsync(cancellationToken);
                    }
                    else
                    {
                        await importer.WriteAsync(row.Currency, NpgsqlDbType.Text, cancellationToken);
                    }
                }
            }
        }
        catch (DecoderFallbackException)
        {
            parser.FailEncoding();
        }

        // Without this, disposing the importer cancels the COPY silently.
        await importer.CompleteAsync(cancellationToken);
    }

    /// <summary>
    /// Staging holds only rows without other errors, so every key that occurs there more than once is a duplicate group.
    /// </summary>
    private static async Task AddDuplicateKeyErrorsAsync(TenantScope scope, ErrorCollector errors, CancellationToken cancellationToken)
    {
        var hasDuplicates = await scope.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM staging GROUP BY employee_id, component, effective_from HAVING count(*) > 1)",
            transaction: scope.Transaction,
            commandTimeout: CommandTimeoutSeconds,
            cancellationToken: cancellationToken));
        if (!hasDuplicates)
        {
            return;
        }

        var duplicates = (await scope.Connection.QueryAsync<DuplicateRow>(new CommandDefinition(
            $"""
            SELECT row_no AS RowNo, count(*) OVER () AS Total
            FROM (SELECT row_no, count(*) OVER (PARTITION BY employee_id, component, effective_from) AS n FROM staging) d
            WHERE n > 1
            ORDER BY row_no
            LIMIT {ErrorCollector.MaxKept}
            """,
            transaction: scope.Transaction,
            commandTimeout: CommandTimeoutSeconds,
            cancellationToken: cancellationToken))).AsList();

        foreach (var duplicate in duplicates)
        {
            errors.Add(
                new IngestionError(duplicate.RowNo, null, ErrorCodes.DuplicateKeyInFile, "Another row of the file has the same employee_id, component and effective_from."),
                ErrorCollector.RowLevelOrder);
        }

        // The rows beyond the first 100 come after every listed duplicate, so they only need to be counted.
        errors.AddUnlisted(checked((int)duplicates[0].Total) - duplicates.Count);
    }

    private static Task<int> InsertBatchAsync(TenantScope scope, Guid tenantId, IngestionBatch batch, CancellationToken cancellationToken) =>
        ExecuteAsync(
            scope,
            """
            INSERT INTO ingestion_batches (id, tenant_id, status, received_at, completed_at,
                                           total_rows, inserted_rows, corrected_rows, unchanged_rows, error_count, errors)
            VALUES (@BatchId, @TenantId, @Status, @ReceivedAt, @CompletedAt,
                    @Total, @Inserted, @Corrected, @Unchanged, @ErrorCount, @Errors::jsonb)
            """,
            new
            {
                batch.BatchId,
                TenantId = tenantId,
                batch.Status,
                batch.ReceivedAt,
                batch.CompletedAt,
                batch.Rows.Total,
                batch.Rows.Inserted,
                batch.Rows.Corrected,
                batch.Rows.Unchanged,
                batch.ErrorCount,
                Errors = JsonSerializer.Serialize(batch.Errors, ErrorJsonOptions),
            },
            cancellationToken);

    private static Task<int> ExecuteAsync(TenantScope scope, string sql, object? parameters, CancellationToken cancellationToken) =>
        scope.Connection.ExecuteAsync(new CommandDefinition(
            sql, parameters, scope.Transaction, CommandTimeoutSeconds, cancellationToken: cancellationToken));

    /// <summary>The current UTC time in whole milliseconds, so a batch reads back exactly as it was returned.</summary>
    private static DateTime UtcNow()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
    }

    private sealed class DuplicateRow
    {
        public int RowNo { get; set; }

        public long Total { get; set; }
    }

    private sealed class BatchRecord
    {
        public Guid BatchId { get; set; }

        public string Status { get; set; } = "";

        public DateTime ReceivedAt { get; set; }

        public DateTime CompletedAt { get; set; }

        public int Total { get; set; }

        public int Inserted { get; set; }

        public int Corrected { get; set; }

        public int Unchanged { get; set; }

        public int ErrorCount { get; set; }

        public string ErrorsJson { get; set; } = "[]";
    }
}
