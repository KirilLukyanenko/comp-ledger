using System.Text;
using CompLedger.Core.Ingestion;
using CompLedger.Infrastructure.Db;
using CompLedger.Infrastructure.Ingestion;
using Dapper;

namespace CompLedger.IntegrationTests;

/// <summary>The ingestion pipeline against a real database, through the non-superuser app login.</summary>
public sealed class PostgresIngestorTests(PostgresFixture fixture)
{
    private const string Header = "employee_id,component,effective_from,amount,currency\n";

    private const string A1 = Header
        + "E-1001,BASE_SALARY,2026-01-01,50000,EUR\n"
        + "E-1001,BASE_SALARY,2026-07-01,55000,EUR\n"
        + "E-1001,BONUS_TARGET_PCT,2026-01-01,10,\n";

    private readonly PostgresIngestor _ingestor = new(fixture.AppDataSource);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ValueRow
    {
        public string EmployeeId { get; set; } = "";

        public string Component { get; set; } = "";

        public DateTime EffectiveFrom { get; set; }

        public decimal Amount { get; set; }

        public string? Currency { get; set; }

        public Guid BatchId { get; set; }

        public DateTime RecordedAt { get; set; }

        public DateTime? SupersededAt { get; set; }

        public Guid? SupersededByBatchId { get; set; }
    }

    private Task<IngestionBatch> IngestAsync(Guid tenant, string csv) => IngestAsync(tenant, Encoding.UTF8.GetBytes(csv));

    private async Task<IngestionBatch> IngestAsync(Guid tenant, byte[] bytes)
    {
        using var body = new MemoryStream(bytes);
        return await _ingestor.IngestAsync(tenant, body, Ct);
    }

    private async Task<List<ValueRow>> ValuesAsync(Guid tenant)
    {
        await using var scope = await TenantScope.BeginAsync(fixture.AppDataSource, tenant, Ct);
        var rows = await scope.Connection.QueryAsync<ValueRow>(
            """
            SELECT employee_id AS EmployeeId, component AS Component, effective_from AS EffectiveFrom, amount AS Amount,
                   currency::text AS Currency, batch_id AS BatchId, recorded_at AS RecordedAt,
                   superseded_at AS SupersededAt, superseded_by_batch_id AS SupersededByBatchId
            FROM compensation_values
            ORDER BY employee_id, component, effective_from, id
            """,
            transaction: scope.Transaction);
        return rows.AsList();
    }

    private static IEnumerable<(int? Row, string? Column, string Code)> Report(IngestionBatch batch) =>
        batch.Errors.Select(e => (e.Row, e.Column, e.Code));

    // ---------- inserted, unchanged, corrected ----------

    [Fact]
    public async Task New_keys_are_inserted_with_one_apply_time()
    {
        var tenant = await fixture.CreateTenantAsync();

        var batch = await IngestAsync(tenant, A1);

        Assert.Equal(BatchStatus.Applied, batch.Status);
        Assert.Equal(new BatchRows(3, 3, 0, 0), batch.Rows);
        Assert.Equal(0, batch.ErrorCount);
        Assert.Empty(batch.Errors);
        Assert.True(batch.CompletedAt >= batch.ReceivedAt);

        var values = await ValuesAsync(tenant);
        Assert.Equal(3, values.Count);
        Assert.All(values, v => Assert.Equal(batch.BatchId, v.BatchId));
        Assert.All(values, v => Assert.Null(v.SupersededAt));
        Assert.Single(values.Select(v => v.RecordedAt).Distinct());
        Assert.Equal([("BASE_SALARY", 50000m, "EUR"), ("BASE_SALARY", 55000m, "EUR"), ("BONUS_TARGET_PCT", 10m, null)],
            values.Select(v => (v.Component, v.Amount, v.Currency)));
    }

    [Fact]
    public async Task Uploading_the_same_content_again_changes_nothing()
    {
        var tenant = await fixture.CreateTenantAsync();
        await IngestAsync(tenant, A1);
        var before = await ValuesAsync(tenant);

        // Same values written differently: amounts compare numerically.
        var batch = await IngestAsync(tenant, A1.Replace("50000,", "50000.00,").Replace(",10,", ",10.0,"));

        Assert.Equal(BatchStatus.Applied, batch.Status);
        Assert.Equal(new BatchRows(3, 0, 0, 3), batch.Rows);
        var after = await ValuesAsync(tenant);
        Assert.Equal(before.Select(v => (v.BatchId, v.RecordedAt, v.Amount, v.SupersededAt)), after.Select(v => (v.BatchId, v.RecordedAt, v.Amount, v.SupersededAt)));
    }

    [Fact]
    public async Task A_different_amount_supersedes_the_old_value_and_keeps_it()
    {
        var tenant = await fixture.CreateTenantAsync();
        var first = await IngestAsync(tenant, A1);

        var batch = await IngestAsync(tenant, Header + "E-1001,BASE_SALARY,2026-01-01,51000,EUR\n");

        Assert.Equal(new BatchRows(1, 0, 1, 0), batch.Rows);
        var values = (await ValuesAsync(tenant)).Where(v => v.Component == "BASE_SALARY" && v.EffectiveFrom == new DateTime(2026, 1, 1)).ToList();
        Assert.Equal(2, values.Count);

        var old = Assert.Single(values, v => v.Amount == 50000m);
        var current = Assert.Single(values, v => v.Amount == 51000m);
        Assert.Equal(first.BatchId, old.BatchId);
        Assert.NotNull(old.SupersededAt);
        Assert.Equal(batch.BatchId, old.SupersededByBatchId);
        Assert.Equal(batch.BatchId, current.BatchId);
        Assert.Null(current.SupersededAt);
        Assert.Null(current.SupersededByBatchId);

        // One apply time per batch: the old row's end is the new row's start, and never before its own start.
        Assert.Equal(current.RecordedAt, old.SupersededAt);
        Assert.True(old.SupersededAt >= old.RecordedAt);
    }

    [Fact]
    public async Task A_different_currency_is_a_correction_too()
    {
        var tenant = await fixture.CreateTenantAsync();
        await IngestAsync(tenant, A1);

        var batch = await IngestAsync(tenant, Header + "E-1001,BASE_SALARY,2026-01-01,50000,USD\nE-1001,BASE_SALARY,2026-07-01,55000.0,EUR\n");

        Assert.Equal(new BatchRows(2, 0, 1, 1), batch.Rows);
    }

    [Fact]
    public async Task One_batch_can_insert_correct_and_leave_unchanged()
    {
        var tenant = await fixture.CreateTenantAsync();
        await IngestAsync(tenant, A1);

        var batch = await IngestAsync(
            tenant,
            Header
            + "E-1001,BASE_SALARY,2026-04-01,52000,EUR\n" // back-dated new key
            + "E-1001,BASE_SALARY,2026-07-01,56000,EUR\n" // corrected
            + "E-1001,BONUS_TARGET_PCT,2026-01-01,10.0000,\n" // unchanged
            + "E-1002,ALLOWANCE,2026-01-01,1200,EUR\n"); // new employee

        Assert.Equal(new BatchRows(4, 2, 1, 1), batch.Rows);
        var values = await ValuesAsync(tenant);
        Assert.Equal(6, values.Count);
        Assert.Equal(5, values.Count(v => v.SupersededAt is null));
    }

    [Fact]
    public async Task Tenants_do_not_share_keys()
    {
        var tenantA = await fixture.CreateTenantAsync("A");
        var tenantB = await fixture.CreateTenantAsync("B");
        await IngestAsync(tenantA, A1);

        var batch = await IngestAsync(tenantB, Header + "E-1001,BASE_SALARY,2026-01-01,70000,USD\n");

        Assert.Equal(new BatchRows(1, 1, 0, 0), batch.Rows);
        Assert.Equal(3, (await ValuesAsync(tenantA)).Count(v => v.SupersededAt is null));
        Assert.Null(await _ingestor.FindBatchAsync(tenantA, batch.BatchId, Ct));
        Assert.NotNull(await _ingestor.FindBatchAsync(tenantB, batch.BatchId, Ct));
    }

    // ---------- rejected ----------

    [Fact]
    public async Task One_bad_row_rejects_the_whole_file_and_the_rejection_is_recorded()
    {
        var tenant = await fixture.CreateTenantAsync();

        var batch = await IngestAsync(
            tenant,
            Header
            + "E-2001,BASE_SALARY,2026-01-01,40000,EUR\n"
            + "E-2002,BASE_SALARY,2026-01-01,41000,EUR\n"
            + "E-2003,BASE_SALARY,2026-01-01,\"5,000\",EUR\n");

        Assert.Equal(BatchStatus.Rejected, batch.Status);
        Assert.Equal(new BatchRows(3, 0, 0, 0), batch.Rows);
        Assert.Equal(1, batch.ErrorCount);
        Assert.Equal([(4, "amount", ErrorCodes.InvalidAmount)], Report(batch));
        Assert.Empty(await ValuesAsync(tenant));

        var stored = await _ingestor.FindBatchAsync(tenant, batch.BatchId, Ct);
        Assert.NotNull(stored);
        Assert.Equal(batch with { Errors = [] }, stored with { Errors = [] });
        Assert.Equal(batch.Errors, stored.Errors);
    }

    [Fact]
    public async Task An_applied_batch_reads_back_as_it_was_returned()
    {
        var tenant = await fixture.CreateTenantAsync();
        var batch = await IngestAsync(tenant, A1);

        var stored = await _ingestor.FindBatchAsync(tenant, batch.BatchId, Ct);

        Assert.NotNull(stored);
        Assert.Equal(batch with { Errors = [] }, stored with { Errors = [] });
        Assert.Empty(stored.Errors);
        Assert.Equal(DateTimeKind.Utc, stored.ReceivedAt.Kind);
        Assert.Null(await _ingestor.FindBatchAsync(tenant, Guid.CreateVersion7(), Ct));
    }

    [Fact]
    public async Task Rows_sharing_a_key_all_get_duplicate_key_in_file()
    {
        var tenant = await fixture.CreateTenantAsync();

        var batch = await IngestAsync(
            tenant,
            Header
            + "E-3001,BASE_SALARY,2026-01-01,100,EUR\n" // 2
            + "E-3002,BASE_SALARY,2026-01-01,100,EUR\n" // 3
            + "E-3001,BASE_SALARY,2026-02-01,100,EUR\n" // 4: other date, not a duplicate
            + "E-3001,BASE_SALARY,2026-01-01,200,EUR\n" // 5
            + "e-3001,BASE_SALARY,2026-01-01,100,EUR\n" // 6: ids are case-sensitive, not a duplicate
            + "E-3001,BASE_SALARY,2026-01-01,100,EUR\n"); // 7: identical row, still a duplicate

        Assert.Equal(BatchStatus.Rejected, batch.Status);
        Assert.Equal([(2, null, ErrorCodes.DuplicateKeyInFile), (5, null, ErrorCodes.DuplicateKeyInFile), (7, null, ErrorCodes.DuplicateKeyInFile)], Report(batch));
        Assert.Equal(3, batch.ErrorCount);
        Assert.Equal(6, batch.Rows.Total);
        Assert.Empty(await ValuesAsync(tenant));
    }

    [Fact]
    public async Task Rows_with_other_errors_are_not_part_of_a_duplicate_group()
    {
        var tenant = await fixture.CreateTenantAsync();

        var batch = await IngestAsync(
            tenant,
            Header
            + "E-1,BASE_SALARY,2026-01-01,100,EUR\n" // 2: valid, and the only valid row with this key
            + "E-1,BASE_SALARY,2026-01-01,100,eur\n" // 3: same key but has its own error
            + "E-2,ALLOWANCE,2026-01-01,5,EUR\n" // 4
            + "E-9,BASE_SALARY,2026-01-01,-1,EUR\n" // 5
            + "E-2,ALLOWANCE,2026-01-01,6,EUR\n"); // 6

        Assert.Equal(
            [
                (3, "currency", ErrorCodes.InvalidCurrency),
                (4, null, ErrorCodes.DuplicateKeyInFile),
                (5, "amount", ErrorCodes.InvalidAmount),
                (6, null, ErrorCodes.DuplicateKeyInFile),
            ],
            Report(batch));
    }

    [Fact]
    public async Task Errors_are_capped_at_100_in_report_order_and_all_are_counted()
    {
        var tenant = await fixture.CreateTenantAsync();
        var csv = new StringBuilder(Header);
        for (var i = 0; i < 150; i++)
        {
            // Rows 2..301: pairs of duplicates, except every tenth pair, where both rows have a bad amount.
            var amount = i % 10 == 9 ? "x" : "1";
            csv.Append($"E-{i},BASE_SALARY,2026-01-01,{amount},EUR\n");
            csv.Append($"E-{i},BASE_SALARY,2026-01-01,{amount},EUR\n");
        }

        var batch = await IngestAsync(tenant, csv.ToString());

        Assert.Equal(BatchStatus.Rejected, batch.Status);
        Assert.Equal(300, batch.ErrorCount);
        Assert.Equal(300, batch.Rows.Total);
        Assert.Equal(100, batch.Errors.Count);
        Assert.Equal(Enumerable.Range(2, 100).Cast<int?>(), batch.Errors.Select(e => e.Row));
        Assert.Equal((20, "amount", ErrorCodes.InvalidAmount), Report(batch).ElementAt(18));
        Assert.Equal((22, null, ErrorCodes.DuplicateKeyInFile), Report(batch).ElementAt(20));

        var stored = await _ingestor.FindBatchAsync(tenant, batch.BatchId, Ct);
        Assert.Equal(300, stored!.ErrorCount);
        Assert.Equal(batch.Errors, stored.Errors);
    }

    // ---------- CSV format through the real reader ----------

    [Fact]
    public async Task Quoted_fields_may_hold_commas_quotes_and_line_breaks_and_rows_count_records()
    {
        var tenant = await fixture.CreateTenantAsync();

        var batch = await IngestAsync(
            tenant,
            "\"employee_id\",component,\"effective_\r\nfrom\",amount,currency\r\n" // header with an unknown multi-line name
            + "E-1,BASE_SALARY,2026-01-01,1,EUR\r\n");
        Assert.Equal([(1, "effective_\r\nfrom", ErrorCodes.UnknownColumn), (1, "effective_from", ErrorCodes.MissingColumn)], Report(batch));
        Assert.Equal(0, batch.Rows.Total);

        batch = await IngestAsync(
            tenant,
            "employee_id,component,effective_from,amount,currency\r\n"
            + "\"E-1\r\n\",\" BASE_SALARY\",\"2026-01-01\",\"1\n\",\"EUR\"\r\n" // 2: one record over three lines, valid after trimming
            + "E-2,BASE_SALARY,2026-01-01,\"1,5\",EUR\r\n" // 3: quoted comma
            + "E-3,\"BASE\"\"SALARY\",2026-01-01,1,EUR\r\n" // 4: doubled quote
            + "E-4,BASE_SALARY,2026-01-01,1,EUR"); // 5: no final line break

        Assert.Equal([(3, "amount", ErrorCodes.InvalidAmount), (4, "component", ErrorCodes.UnknownComponent)], Report(batch));
        Assert.Equal(4, batch.Rows.Total);
    }

    [Fact]
    public async Task Crlf_files_with_a_bom_and_trailing_blank_lines_are_applied()
    {
        var tenant = await fixture.CreateTenantAsync();
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(A1.Replace("\n", "\r\n") + "\r\n  \r\n\t\r\n\"\"\r\n\r\n")];

        var batch = await IngestAsync(tenant, bytes);

        Assert.Equal(BatchStatus.Applied, batch.Status);
        Assert.Equal(new BatchRows(3, 3, 0, 0), batch.Rows);
    }

    [Fact]
    public async Task Blank_lines_before_the_last_data_record_are_malformed_rows()
    {
        var tenant = await fixture.CreateTenantAsync();

        var batch = await IngestAsync(
            tenant,
            Header
            + "\n" // 2
            + "E-1,BASE_SALARY,2026-01-01,1,EUR\n" // 3
            + "   \n" // 4
            + "\"\"\n" // 5: a lone "" is a blank line
            + "E-2,BASE_SALARY,2026-01-01,1,EUR\n" // 6
            + "\n\n");

        Assert.Equal([(2, null, ErrorCodes.MalformedCsv), (4, null, ErrorCodes.MalformedCsv), (5, null, ErrorCodes.MalformedCsv)], Report(batch));
        Assert.Equal(5, batch.Rows.Total);
    }

    [Theory]
    [InlineData("E-1,BASE_SALARY,2026-01-01,1\n")] // too few fields
    [InlineData("E-1,BASE_SALARY,2026-01-01,1,EUR,\n")] // too many fields
    [InlineData("E-1,BASE_SALARY,2026-01-01,1\"0,EUR\n")] // stray quote in an unquoted field
    [InlineData("E-1,BASE_SALARY,2026-01-01,\"1\"0,EUR\n")] // text after a closing quote
    public async Task Unparseable_rows_are_malformed_and_get_no_other_error(string badRow)
    {
        var tenant = await fixture.CreateTenantAsync();

        var batch = await IngestAsync(tenant, Header + "E-0,BASE_SALARY,2026-01-01,1,EUR\n" + badRow);

        Assert.Equal([(3, null, ErrorCodes.MalformedCsv)], Report(batch));
        Assert.Equal(2, batch.Rows.Total);
    }

    [Theory]
    [InlineData("\n" + Header + "E-1,BASE_SALARY,2026-01-01,1,EUR\n")] // blank first record
    [InlineData("  \n\n" + Header)]
    [InlineData("employee_id,compo\"nent,effective_from,amount,currency\nE-1,BASE_SALARY,2026-01-01,1,EUR\n")] // stray quote
    public async Task A_blank_or_unparseable_first_record_is_a_malformed_header(string csv)
    {
        var tenant = await fixture.CreateTenantAsync();

        var batch = await IngestAsync(tenant, csv);

        Assert.Equal([(1, null, ErrorCodes.MalformedCsv)], Report(batch));
        Assert.Equal(1, batch.ErrorCount);
        Assert.Equal(0, batch.Rows.Total);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t\n\n")]
    [InlineData(Header)]
    [InlineData(Header + "\n \n")]
    public async Task A_file_without_data_rows_is_rejected_as_empty(string csv)
    {
        var tenant = await fixture.CreateTenantAsync();
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(csv)];

        foreach (var bytes in new[] { Encoding.UTF8.GetBytes(csv), withBom })
        {
            var batch = await IngestAsync(tenant, bytes);

            Assert.Equal(BatchStatus.Rejected, batch.Status);
            Assert.Equal([(null, null, ErrorCodes.EmptyFile)], Report(batch));
            Assert.Equal(0, batch.Rows.Total);
        }
    }

    [Fact]
    public async Task Header_errors_stop_processing_before_any_data_row()
    {
        var tenant = await fixture.CreateTenantAsync();

        var batch = await IngestAsync(tenant, "employee_id,component,effective_from,amount\nE-1,NOPE,bad,bad\n");

        Assert.Equal([(1, "currency", ErrorCodes.MissingColumn)], Report(batch));
        Assert.Equal(0, batch.Rows.Total);
    }

    [Fact]
    public async Task Invalid_utf8_is_a_file_level_error_and_earlier_row_errors_are_kept()
    {
        var tenant = await fixture.CreateTenantAsync();

        // The reader decodes in 64 KiB buffers. Put a bad row early, then enough valid rows that the bad bytes fall
        // into a later buffer, so the early rows were fully parsed when decoding fails.
        var csv = new StringBuilder(Header + "E-0,BASE_SALARY,2026-01-01,x,EUR\n");
        for (var i = 1; i <= 8000; i++)
        {
            csv.Append($"E-{i},BASE_SALARY,2026-01-01,1,EUR\n");
        }

        byte[] bytes = [.. Encoding.UTF8.GetBytes(csv.ToString()), .. "E-X,BASE_SALARY,2026-01-01,1,EUR"u8, 0xC3, 0x28, (byte)'\n'];

        var batch = await IngestAsync(tenant, bytes);

        Assert.Equal(BatchStatus.Rejected, batch.Status);
        Assert.Equal([(null, null, ErrorCodes.InvalidEncoding), (2, "amount", ErrorCodes.InvalidAmount)], Report(batch));
        Assert.Equal(2, batch.ErrorCount);
        Assert.InRange(batch.Rows.Total, 1, 8001);
        Assert.Empty(await ValuesAsync(tenant));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE })] // UTF-16 LE BOM
    [InlineData(new byte[] { 0xFE, 0xFF })] // UTF-16 BE BOM
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0x00 })] // UTF-32 LE BOM
    [InlineData(new byte[] { 0x00, 0x00, 0xFE, 0xFF })] // UTF-32 BE BOM
    public async Task Other_byte_order_marks_are_not_recognised(byte[] bom)
    {
        var tenant = await fixture.CreateTenantAsync();
        byte[] bytes = [.. bom, .. Encoding.Unicode.GetBytes(A1)];

        var batch = await IngestAsync(tenant, bytes);

        Assert.Equal([(null, null, ErrorCodes.InvalidEncoding)], Report(batch));
        Assert.Equal(0, batch.Rows.Total);
    }

    [Fact]
    public async Task A_multi_byte_sequence_cut_off_at_the_end_of_the_file_is_invalid_encoding()
    {
        var tenant = await fixture.CreateTenantAsync();
        byte[] bytes = [.. Encoding.UTF8.GetBytes(A1), 0xC3];

        var batch = await IngestAsync(tenant, bytes);

        Assert.Equal(BatchStatus.Rejected, batch.Status);
        Assert.Equal([(null, null, ErrorCodes.InvalidEncoding)], Report(batch));
        Assert.Empty(await ValuesAsync(tenant));
    }

    [Fact]
    public async Task Latin1_bytes_are_invalid_encoding()
    {
        var tenant = await fixture.CreateTenantAsync();

        var batch = await IngestAsync(tenant, Encoding.Latin1.GetBytes(Header + "É-1,BASE_SALARY,2026-01-01,1,EUR\n"));

        Assert.Equal([(null, null, ErrorCodes.InvalidEncoding)], Report(batch));
    }

    // ---------- concurrency ----------

    [Fact]
    public async Task Two_concurrent_batches_of_one_tenant_give_the_same_result_as_sequential_ones()
    {
        const int keys = 2000;
        static string File(string amount)
        {
            var csv = new StringBuilder(Header);
            for (var i = 0; i < keys; i++)
            {
                csv.Append($"E-{i},BASE_SALARY,2026-01-01,{amount},EUR\n");
            }

            return csv.ToString();
        }

        for (var round = 0; round < 3; round++)
        {
            var tenant = await fixture.CreateTenantAsync();
            await IngestAsync(tenant, File("1"));

            var batches = await Task.WhenAll(
                Task.Run(() => IngestAsync(tenant, File("2")), Ct),
                Task.Run(() => IngestAsync(tenant, File("3")), Ct));

            // Whichever went first corrected every 1; the other then corrected every value of the first.
            // Without the tenant lock, the second would find the rows already superseded and report something else.
            Assert.All(batches, b => Assert.Equal(new BatchRows(keys, 0, keys, 0), b.Rows));

            var values = await ValuesAsync(tenant);
            Assert.Equal(3 * keys, values.Count);
            var current = values.Where(v => v.SupersededAt is null).ToList();
            Assert.Equal(keys, current.Count);
            Assert.Equal(keys, current.Select(v => v.EmployeeId).Distinct().Count());

            // All current rows come from one batch: the one applied last.
            var winner = Assert.Single(current.Select(v => v.BatchId).Distinct());
            var loser = batches.Single(b => b.BatchId != winner).BatchId;
            var superseded = values.Where(v => v.SupersededAt is not null).ToList();
            Assert.All(superseded, v => Assert.True(v.SupersededAt >= v.RecordedAt, "superseded_at must not be before recorded_at"));
            Assert.All(superseded.Where(v => v.BatchId == loser), v => Assert.Equal(winner, v.SupersededByBatchId));
            Assert.All(superseded.Where(v => v.BatchId != loser), v => Assert.Equal(loser, v.SupersededByBatchId));

            // Apply times follow the order in which the batches were applied.
            var loserAppliedAt = Assert.Single(superseded.Where(v => v.BatchId == loser).Select(v => v.RecordedAt).Distinct());
            var winnerAppliedAt = Assert.Single(current.Select(v => v.RecordedAt).Distinct());
            Assert.True(winnerAppliedAt > loserAppliedAt);
        }
    }

    [Fact]
    public async Task The_tenant_lock_is_not_held_while_a_body_is_still_arriving()
    {
        var tenant = await fixture.CreateTenantAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var slowBody = new GatedStream(Encoding.UTF8.GetBytes(A1), release.Task);

        // The slow upload has started and is stuck in the middle of its body.
        var slow = Task.Run(() => _ingestor.IngestAsync(tenant, slowBody, Ct), Ct);
        await slowBody.Blocked.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // Another upload of the same tenant goes through meanwhile.
        var fast = await IngestAsync(tenant, Header + "E-9,ALLOWANCE,2026-01-01,1,EUR\n").WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(BatchStatus.Applied, fast.Status);
        Assert.False(slow.IsCompleted);

        release.SetResult();
        Assert.Equal(new BatchRows(3, 3, 0, 0), (await slow).Rows);
    }

    [Fact]
    public async Task A_tenant_waiting_for_its_lock_does_not_hold_up_another_tenant()
    {
        var tenantA = await fixture.CreateTenantAsync("A");
        var tenantB = await fixture.CreateTenantAsync("B");

        // Hold tenant A's lock (the key from PRP-001 7.2) from outside, as a long-running batch of A would.
        await using var holder = await fixture.AppDataSource.OpenConnectionAsync(Ct);
        await using var holderTransaction = await holder.BeginTransactionAsync(Ct);
        await holder.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@Tenant, 0))", new { Tenant = tenantA.ToString("D") }, holderTransaction);

        var blocked = Task.Run(() => IngestAsync(tenantA, A1), Ct);

        // B is applied while A is still waiting: the lock is per tenant, not global.
        var other = await IngestAsync(tenantB, A1).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(new BatchRows(3, 3, 0, 0), other.Rows);
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        Assert.False(blocked.IsCompleted, "A batch of tenant A must wait for tenant A's lock.");
        Assert.Empty(await ValuesAsync(tenantA));

        await holderTransaction.CommitAsync(Ct);
        Assert.Equal(new BatchRows(3, 3, 0, 0), (await blocked.WaitAsync(TimeSpan.FromSeconds(30), Ct)).Rows);
    }

    [Fact]
    public async Task Batches_of_different_tenants_run_in_parallel()
    {
        var tenants = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            tenants.Add(await fixture.CreateTenantAsync());
        }

        var batches = await Task.WhenAll(tenants.Select(t => Task.Run(() => IngestAsync(t, A1), Ct)));

        Assert.All(batches, b => Assert.Equal(new BatchRows(3, 3, 0, 0), b.Rows));
        foreach (var tenant in tenants)
        {
            Assert.Equal(3, (await ValuesAsync(tenant)).Count);
        }
    }

    /// <summary>Serves the first half of its bytes, then waits for the gate before serving the rest.</summary>
    private sealed class GatedStream(byte[] bytes, Task gate) : Stream
    {
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        public Task Blocked => _blocked.Task;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var half = bytes.Length / 2;
            if (_position >= half)
            {
                _blocked.TrySetResult();
                await gate.WaitAsync(cancellationToken);
            }

            var limit = _position < half ? half : bytes.Length;
            var count = Math.Min(buffer.Length, limit - _position);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Synchronous reads are not allowed.");

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
