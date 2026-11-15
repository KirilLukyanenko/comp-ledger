# PRP-004: Streaming export

| Field      | Value |
|------------|-------|
| Status     | Draft |
| Version    | 1.1 |
| Owner      | KirilLukyanenko |
| Created    | 2026-11-15 |
| Depends on | PRP-001 (2.1, implemented), PRP-002 (1.3, implemented), PRP-003 (1.2, implemented: not in the export), PRP-005 (2.1, implemented: its tools must not change) |
| Unlocks    | Computed fields in the export (a later PRP-004 version), export over MCP (a later PRP-005 version) |

---

## 1. Goal

A tenant can download, as one CSV file, every compensation value in effect on a date for all its employees: as known now, as known at an instant, or through a snapshot. The file is streamed: the service never holds the whole file in memory, a 1M-row export takes the same memory as a small one, and a slow download never holds up the tenant's uploads. Every export names the instant it was taken at, so asking again with that instant gives the same file, byte for byte.

## 2. Why

- **Business.** Payroll, finance and review tools take files, not one API call per employee. A review cycle freezes its input as a snapshot (PRP-002); this PRP lets the reviewers take that input out of the service in one request. An auditor who gets the file can ask for it again later and get exactly the same bytes.
- **Engineering.** PRP-001 streams uploads in; this PRP streams the same data out. PRP-002's knowledge time makes a long read stable without holding a lock for the whole download: the export fixes its instant first, then reads "as known at" that instant, which no later batch can change.
- **Scale.** A large tenant has millions of values. Building the file in memory, or paging through employees one request at a time, does not scale; one ordered SQL statement streamed to the client does.

## 3. What

### 3.1 Concepts

- **Export**: a CSV file with one row per value in effect on a date `asOf`, among the values known at an instant `knownAt` (PRP-002 3.1), for every employee of the tenant.
- **Export instant**: the `knownAt` the export is taken at. Given by the client, or, when not given, the request time (PRP-002 3.1) read after the wait in 3.4 item 3. For a snapshot export it is the snapshot's `knownAt`.
- **Same file**: an export for a given `(asOf, knownAt)` has the same bytes whenever it is asked for (3.4 item 4).

### 3.2 Inputs and validation

**`GET /api/v1/compensation/export`** names two query parameters:

- `asOf`: optional, the PRP-001 rules (3.6): a real `yyyy-MM-dd` date from 1900-01-01 to 2999-12-31, not trimmed, empty is invalid. When absent, the UTC date of the request, as on the PRP-001 as-of read ("today", PRP-001 3.1).
- `knownAt`: optional, the PRP-002 rules (3.2): format, real instant, from `1900-01-01T00:00:00Z` up to the request time inclusive, not trimmed, empty is invalid.
- Either given more than once gives `400`, even with equal values. Any violation gives `400`.

**`GET /api/v1/snapshots/{snapshotId}/export`** names `asOf` and `knownAt` only to refuse them: giving either (any value, even empty) gives `400`, as on the snapshot read (PRP-002 3.4 item 6). `snapshotId` follows PRP-002 3.2: hyphenated UUID, either case, anything else is `404` after the `401` and `400` checks.

**Other query parameters** are ignored on both endpoints, as in PRP-001 3.6. `include` is not a parameter of either export: `include=computed` and any other `include` value are ignored, and the file is the same as without it.

**Request headers.** `Accept` is ignored: the response is always CSV. Whether a differently cased parameter name (`ASOF`) is the same parameter is not part of the contract, as for the PRP-001 and PRP-002 parameters.

### 3.3 Error responses

Every error is RFC 7807 `ProblemDetails`, decided **before the first byte of the file is sent**, with the PRP-001 and PRP-002 exceptions (Kestrel's `400` for `%00` in a path).

| Status | When |
|--------|------|
| `400` | Invalid or repeated `asOf` or `knownAt`; `knownAt` later than the request time (after the wait, 3.4 item 3); `asOf` or `knownAt` given on a snapshot export |
| `401` | Tenant resolution, exactly as in PRP-001 3.6 |
| `404` | Snapshot export: unknown or foreign snapshot, malformed `snapshotId` |
| `405` | Known path, wrong method (PRP-001 3.6 rule) |

Order of checks: tenant (`401`), then query validation (`400`), then the snapshot lookup (`404`), then the wait and the `knownAt`-after-request-time check (`400`).

`/api/v1/snapshots/{anything}/export` is a known path for any segment, as the PRP-002 snapshot paths are (PRP-002 3.2): a wrong method on it is `405` with `Allow: GET`, also when the segment is not a valid `snapshotId`.

A tenant with no values, or none in effect on `asOf` as known at the instant, is `200` with the header line only. It is never `404`.

**After the first byte**, the status is already `200` and cannot change. If the export fails or is cancelled after that, the service aborts the connection without writing the rest of the file and without a ProblemDetails body. A file the client received in full always ends with a line break after its last row (3.5); the client detects a failed export by the aborted connection.

### 3.4 Semantics

1. **Rows.** One row per value that is known at the export instant and in effect on `asOf`, for every employee of the tenant. Periods are computed among the values known at the instant, exactly as the PRP-002 as-known-at as-of read computes them: a row's `effective_to` is the day before the next later `effective_from` of the same employee and component among those values, or empty.
2. **The same values as the reads.** For every employee, the rows of the export are exactly the `components` of the as-of read with the same `asOf` and `knownAt` (PRP-002), with the same amounts, currencies and periods. A snapshot export has exactly the rows of the snapshot read for every employee.
3. **The instant is fixed first.** An export without `knownAt` waits while a batch of the tenant is being applied, as a read with `knownAt` does (PRP-002 3.4 item 4), then takes the request time as its instant. An export with `knownAt`, and a snapshot export, wait the same way. After the wait, the export reads the values known at the instant. A batch that commits while the file is being sent is never in it.
4. **Same file.** The export for a given `asOf` and `knownAt` has the same bytes every time it is asked for: the export without `knownAt` returns its instant (3.5), and asking again with that instant as `knownAt` and the same `asOf` gives the same file. A snapshot export is the same file as the export with the snapshot's `asOf` and `knownAt`.
5. **Uploads are not held up.** The export does not hold the tenant's lock while it sends the file. Once its instant is fixed, a batch of the same tenant can take its lock, apply and commit while the export is still being sent, however slowly the client reads.
6. **Streaming.** The service sends the file as it reads it from the database. It never builds the whole file, or all its rows, in memory (3.6).
7. **Tenant isolation.** The export reads only the caller's values, under row-level security, as in PRP-001 and PRP-002. A snapshot of another tenant is `404`.
8. **Nothing else changes.** No PRP-001, PRP-002, PRP-003 or PRP-005 response, rule or log entry changes. The MCP tools are unchanged and there is no export tool.

### 3.5 File format and API contract

Base path and tenant resolution as in PRP-001 3.6.

| Method | Path | Tenant | Result |
|--------|------|--------|--------|
| GET | `/api/v1/compensation/export?asOf=yyyy-MM-dd&knownAt=K` | yes | `200`, the file. Both parameters optional (3.2) |
| GET | `/api/v1/snapshots/{snapshotId}/export` | yes | `200`, the file at the snapshot's `asOf` and `knownAt`. `404` for an unknown snapshot |

**Response headers** on `200`:

- `Content-Type: text/csv; charset=utf-8`.
- `X-As-Of`: the export's `asOf`, `yyyy-MM-dd` (the default date when none was given; the snapshot's on a snapshot export).
- `X-Known-At`: the export instant in the PRP-002 response format, UTC with exactly six fractional digits: `2026-11-15T14:22:31.123456Z`. A `knownAt` the client gave is echoed in that format (`…:09Z` in, `…:09.000000Z` out), as PRP-002 3.4 item 1 does.
- No `Content-Length`, whatever the size of the file, a header-only file included: the length is not known in advance. Compression, `Content-Disposition`, `ETag` and caching headers are not part of the contract.

**File**, in UTF-8 without a BOM:

```
employee_id,component,effective_from,effective_to,amount,currency
E-1001,ALLOWANCE,2026-01-01,,6000,EUR
E-1001,BASE_SALARY,2026-04-01,2026-06-30,52000.5,EUR
E-1001,BONUS_TARGET_PCT,2026-01-01,,10,
E-1002,BASE_SALARY,2025-07-01,,48000,USD
```

- **Header**: exactly `employee_id,component,effective_from,effective_to,amount,currency`, always present, even when there are no rows.
- **Lines** end with LF (`\n`, 0x0A), including the last line. No CR. No blank lines.
- **Fields** are never quoted. No value can contain a comma, a quote or a line break: `employee_id` and `component` follow the PRP-001 patterns, and the other fields are dates, numbers or three letters.
- **`effective_from`, `effective_to`**: `yyyy-MM-dd`. `effective_to` is inclusive, as in PRP-001; empty when the period is open-ended.
- **`amount`**: the stored value in its shortest form: the integer part, then a dot and the fractional digits only when the fractional part is not zero, without trailing zeros. `50000.0000` is written `50000`, `52000.5000` is `52000.5`, `0.0001` is `0.0001`, `0` is `0`. No sign, no grouping, no exponent, always a dot. Every amount written is valid in a PRP-001 upload.
- **`currency`**: three uppercase letters, or empty for a component without one (`BONUS_TARGET_PCT`).
- **Order**: by `employee_id`, then by `component`, both compared ordinally on their UTF-16 code units (as .NET `string.CompareOrdinal`; the values are ASCII, so this is byte order). So `E-10` comes before `E-9`, and `Z-1` before `a-1`. An employee has at most one value in effect per component, so the order is total.

An export file is not an upload file: `effective_to` is an unknown column for PRP-001 ingestion, and loading an export back is out of scope (section 5).

### 3.6 Non-functional budgets

Starting budgets. The first CI run calibrates them; any change is recorded in the changelog with the measured numbers.

| Budget | Target |
|--------|--------|
| Existing budgets | PRP-001 3.7, PRP-002 3.6, PRP-003 3.7 and PRP-005 3.6 still hold. This PRP adds no index and no write |
| Export throughput | On the PRP-002 3.6 tenant (the 1M-row file, then the 100k-row correction), one export as of a date on which every employee's newest value of each component is in effect. The load gate's generator gives each employee and component two dated values, so that export has about half of the 1M values (500,001 rows as of 2025-08-01). From the request to the last byte received: 30 s or less on a GitHub-hosted runner, with the client reading as fast as it can |
| Export memory | Measured in a fresh API process, as PRP-001 3.7 measures uploads. After the tenant above is set up, the API process is restarted; the database keeps its data. One small export (a tenant with a few rows) warms it up, then `VmHWM` (PRP-001 3.7) is read, then the large export above runs, then `VmHWM` is read again. Growth: 64 MiB or less. Peak: 300 MiB or less. Without the restart, `VmHWM` would still hold the 1M-row upload's peak and the growth would read 0 |
| Uploads during an export | The PRP-001 3.7 100k-row tenant (the load gate's small run), exported as of a date on which its newest values are in effect. While that export is open and its client has read only the header line, an upload of one row to the same tenant answers `201` within 10 s |
| Logging | One JSON log entry per export that sent a `200`, written when it ends, with `TenantId`, `SnapshotId` (the id, or `null` for an export without a snapshot), `AsOf` (`yyyy-MM-dd`), `KnownAt` (the response format), `Rows` (data rows written), `Outcome` (`"Completed"` or `"Aborted"`) and `DurationMs`, under `State` as in PRP-001 3.7. No employee id, component, date or amount of any row. Refused exports (`400`, `401`, `404`, `405`) log no entry of their own. Framework logs are outside this rule, as in PRP-002 3.6 |

## 4. Acceptance examples (public)

A separate holdout suite checks the same rules through the HTTP API with different data and more edge cases. **Implement the rules in section 3, not these examples.** Uploaded rows are `employee_id,component,effective_from,amount,currency`; `H` is the export header line; every file below ends with a line break.

**A1. A small export.**
T1 uploads `E-1001,BASE_SALARY,2026-01-01,50000,EUR`, `E-1001,BASE_SALARY,2026-04-01,52000.50,EUR`, `E-1001,BASE_SALARY,2026-07-01,54000,EUR`, `E-1001,BONUS_TARGET_PCT,2026-01-01,10,`, `E-1001,ALLOWANCE,2026-01-01,6000.0000,EUR`, `E-1002,BASE_SALARY,2025-07-01,48000,USD`.
`GET /api/v1/compensation/export?asOf=2026-05-15` gives `200`, `X-As-Of: 2026-05-15`, an `X-Known-At` at or after the batch's `appliedAt`, and the file in 3.5.

**A2. Order is ordinal.**
T2 uploads `a-1`, `E-9`, `Z-1`, `E-10` and `E.1`, each with one `BASE_SALARY` from 2026-01-01. The export as of 2026-02-01 lists them as `E-10`, `E-9`, `E.1`, `Z-1`, `a-1`.

**A3. The same file again.**
Given A1, T1 exports as of 2026-05-15 without `knownAt` and gets `X-Known-At: K`. T1 uploads `E-1001,BONUS_TARGET_PCT,2026-01-01,15,`. The export with `asOf=2026-05-15&knownAt=K` is byte for byte the first file. The export without `knownAt` shows `15` for `BONUS_TARGET_PCT`. The export with `knownAt` equal to A1's `appliedAt` minus one microsecond is `H` only.

**A4. A snapshot export.**
Given A1, T1 creates `{"name": "Review", "asOf": "2026-05-15"}`, then uploads a correction. `GET /api/v1/snapshots/{id}/export` gives the file of the export with the snapshot's `asOf` and `knownAt`, and the headers `X-As-Of: 2026-05-15` and `X-Known-At` equal to the snapshot's `knownAt`. With `?asOf=2026-01-01` or `?knownAt=` it is `400`. An unknown id and a malformed id are `404`. T2 gets `404` for T1's snapshot.

**A5. Validation and empty exports.**
Each gives `400`: `asOf=2026-02-30`, `asOf=`, `asOf=%202026-01-01`, `asOf` given twice, `knownAt=2026-11-13T10:00:00+01:00`, `knownAt` one hour after the request, `knownAt` given twice. A new tenant's export is `200` with `H` only. An export as of a date before every value is `200` with `H` only. `?include=computed` gives the same file as without it.

**A6. Isolation.**
Given A1 and A2, T1's export has no row of T2 and T2's has no row of T1.

**A7. A slow download does not hold up uploads** (integration test and holdout scenario).
T3 has 100,000 values. A client starts T3's export, reads the header line and stops reading. An upload of one new row to T3 answers `201` within 10 s. The client then reads the rest: the file has 100,000 rows and not the new one.

**A8. Waiting for a batch being applied** (integration test, not the holdout suite).
While a 1M-row batch of T1 holds its lock, T1's export without `knownAt` does not send its first byte until the batch commits, and then includes all its rows; its `X-Known-At` is at or after the batch's `appliedAt`. T2's export answers at once.

## 5. Out of scope

- Computed fields in the export (a later version of this PRP); any `include` parameter.
- Exporting the history of values, superseded values or batches; filtering by employee or component; paging.
- Formats other than CSV (JSON, NDJSON, Excel); content negotiation; compression by the service.
- Asynchronous export jobs, stored export files, download links.
- Uploading an export file back, or making the export an upload file.
- An export tool over MCP (a later PRP-005 version).
- Any change to ingestion, the reads, snapshots, computed fields or the MCP tools.
- `HEAD` and `OPTIONS` on the export paths: not part of the contract, as for the PRP-001 endpoints.

## 6. Context for the agent

### 6.1 Read first

- `CLAUDE.md`, `specs/README.md`, and PRP-001, PRP-002, PRP-003 and PRP-005 in full: the export is the PRP-002 as-known-at as-of read for every employee at once.
- ASP.NET Core response streaming and `HttpResponse.BodyWriter`: https://learn.microsoft.com/aspnet/core/fundamentals/middleware/request-response
- Npgsql data reader and `CommandBehavior.SequentialAccess`: https://www.npgsql.org/doc/basic-usage.html
- PostgreSQL collation, `COLLATE "C"`: https://www.postgresql.org/docs/current/collation.html

### 6.2 Current state of the repository

PRP-001 2.1, PRP-002 1.3, PRP-003 1.2 and PRP-005 2.1 are implemented on `main`: migrations `0001` to `0007`, 278 unit and 744 integration tests. The relevant parts:

```
src/CompLedger.Core/Ingestion/RowParser.cs                # date rules (CheckDate), reused for asOf
src/CompLedger.Core/KnownAt.cs                            # parse and format instants
src/CompLedger.Infrastructure/Db/TenantScope.cs           # transaction, set_config, WaitForAppliesAsync (shared lock + clock)
src/CompLedger.Infrastructure/Queries/CompensationQueries.cs   # as-of and history, value filters Current and KnownAtInstant
src/CompLedger.Infrastructure/Snapshots/SnapshotStore.cs
src/CompLedger.Api/Operations/{Compensation,Snapshot}Operations.cs   # parameters, 400/404, shared with MCP
src/CompLedger.Api/Endpoints/{Compensation,Snapshot}Endpoints.cs
tests/CompLedger.IntegrationTests/KnownAtTests.cs         # holds the A8 test of PRP-002 that T0 fixes
```

### 6.3 Target tree after this PRP

```
src/CompLedger.Core/Export/ExportCsv.cs                   # header, row formatting, amount format (pure)
src/CompLedger.Infrastructure/Export/ExportQuery.cs       # fix the instant, then stream the rows
src/CompLedger.Api/Endpoints/ExportEndpoints.cs           # the two endpoints, headers, streaming, logging
tests/CompLedger.UnitTests/ExportCsvTests.cs
tests/CompLedger.IntegrationTests/ExportTests.cs
```

Parameter checks may live in a new `Operations/ExportOperations.cs` or in the endpoint file; reuse the existing `asOf`, `knownAt` and `snapshotId` checks rather than writing new ones.

### 6.4 Technical decisions (fixed: changing one requires a PRP update first)

| Decision | Reason |
|----------|--------|
| Every export is an as-known-at read at a fixed instant; without `knownAt` the instant is the request time after the wait | A long read gets one stable answer, and the instant in `X-Known-At` makes the file reproducible |
| Two transactions: the first takes the PRP-002 shared lock, reads `clock_timestamp()` and commits; the second streams the rows with the known-at filter at that instant, without the lock | After the wait no batch can get an apply time at or before the instant (PRP-002 3.4 item 4), so the answer is stable without holding a lock that would hold up uploads for the whole download (3.4 item 5) |
| One SQL statement returns all rows in file order; the API writes each row as it reads it, through a data reader, never a list | Constant memory for any tenant size (3.6) |
| Order by `employee_id COLLATE "C"`, then `component COLLATE "C"` | Ordinal order regardless of the database's default collation (6.5 #2) |
| Periods with `lead(effective_from)` over the known set, partitioned by employee and component, filtered by known-at **before** the window | The same periods as the PRP-002 read (PRP-002 6.5 #3) |
| Amounts formatted in Core with an invariant, shortest-form format | One exact spelling; no culture, no exponent, no trailing zeros |
| No new index or migration | The export reads the whole tenant once; measure first (T4) |
| No MCP change | PRP-005 says a tool is its endpoint; an export tool needs its own PRP-005 version |

### 6.5 Known gotchas

1. **Do not hold the shared lock while streaming.** PRP-002 reads take `pg_advisory_xact_lock_shared` for the whole transaction. An export that streams inside that transaction keeps the lock until the last byte, and every upload of the tenant waits for the slowest client (A7). Fix the instant in a short first transaction and commit it; stream in a second one.
2. **The database's default collation is not ordinal.** The official PostgreSQL image initialises with `en_US.utf8`, where `ORDER BY employee_id` puts `a-1` next to `A-1` and ignores punctuation at first. Use `COLLATE "C"` on both sort keys (A2). Sorting in .NET instead would need all rows in memory.
3. **Npgsql returns `numeric(19,4)` with scale 4.** `decimal.ToString()` gives `50000.0000`. Format with `ToString("0.####", CultureInfo.InvariantCulture)` or equivalent, never with the current culture (some cultures use a comma).
4. **`currency` is `char(3)` and may be `NULL`.** Write `NULL` as an empty field, not `null` or spaces.
5. **Headers go out with the first write.** Every `400` and `404` must be decided, and the instant fixed, before the first byte of the body is written. After that, an exception must not reach the ProblemDetails handler (it cannot change a status already sent): catch it, log the `Aborted` entry, and abort the connection (`HttpContext.Abort()`), so the client cannot take a partial file for a whole one.
6. **Do not buffer the response.** `Results.Text`, `Results.File` over a `MemoryStream`, a `StringBuilder` for the file, or `ToListAsync()` of the rows all hold the whole file. Write rows to `HttpResponse.BodyWriter` or the body stream, and flush now and then so the first bytes leave early.
7. **Cancellation.** Pass `HttpContext.RequestAborted` to the query. A client that disconnects ends the export: stop reading, log `Aborted`, do not log an error for it.
8. **The long statement holds a connection.** The streaming statement keeps a pooled connection and a database snapshot until the client has read everything. That is accepted in this PRP; the advisory lock is not held (6.5 #1). Use the 600 s command timeout of the ingestor for the lock statement, as PRP-002 6.5 #14 says.
9. **A known-at boundary.** With `knownAt` equal to a batch's `appliedAt`, its values are in the export and the ones it superseded are not (PRP-002 6.5 #4). Reuse the `KnownAtInstant` filter of `CompensationQueries`.
10. **The as-of default.** Without `asOf`, use the same "today (UTC)" as the PRP-001 as-of read, and return it in `X-As-Of`.
11. **The A8 race (T0).** The PRP-002 test `A8_while_a_one_million_row_batch_is_applied_its_tenant_waits_and_another_tenant_answers_at_once` failed twice under load, once in run 6 and once in CI on `main` (run 43706843465, a commit that changed only documentation): the read got `404` instead of `200`. The service is right; the test races. It takes `now` from the database clock as soon as it sees the batch's exclusive lock, but the ingestor reads the batch's apply time (`clock_timestamp()`) in its next statement, after the lock. When that statement runs late, the apply time is after `now`, so the batch is not known at `now` and `404` is the correct answer. The assertion that would have said so, `appliedAt < now`, comes after the one that failed. Fix the test so that it takes `now` only after the apply time is fixed, for example by waiting until the lock holder's backend (`pg_locks.pid`, then `pg_stat_activity`) runs a statement after the apply-time statement. Keep every assertion; do not retry, do not accept `404`, do not change the service.

## 7. Implementation blueprint

### 7.1 Data model

None. No migration, no new column, no new index.

### 7.2 Algorithm

```
GET /api/v1/compensation/export   (or /api/v1/snapshots/{id}/export)
  validate parameters (400); snapshot export: refuse asOf/knownAt (400), look up the snapshot (404)
  transaction 1 (TenantScope):
    requestTime <- WaitForAppliesAsync()                // shared lock, then clock_timestamp()
    K <- knownAt given ? knownAt : (snapshot ? snapshot.knownAt : requestTime)
    if knownAt given and knownAt > requestTime: 400
    commit                                             // the lock ends here
  set headers: 200, text/csv; charset=utf-8, X-As-Of, X-Known-At
  transaction 2 (TenantScope), one statement, read row by row:
    WITH known AS (
      SELECT employee_id, component, effective_from, amount, currency
      FROM compensation_values
      WHERE tenant_id = @TenantId AND recorded_at <= @K AND (superseded_at IS NULL OR superseded_at > @K)
    ), periods AS (
      SELECT *, lead(effective_from) OVER (PARTITION BY employee_id, component ORDER BY effective_from) - 1 AS effective_to
      FROM known
    )
    SELECT ... FROM periods
    WHERE effective_from <= @AsOf AND (effective_to IS NULL OR effective_to >= @AsOf)
    ORDER BY employee_id COLLATE "C", component COLLATE "C"
  write the header line, then one line per row (ExportCsv), flushing now and then
  commit; log one entry (Completed)
  on failure or cancellation after the first byte: log Aborted, abort the connection
```

### 7.3 Tasks (in order; one commit per task, message `PRP-004 Tn: <summary>`)

| #  | Task | Done when |
|----|------|-----------|
| T0 | Fix the A8 race in the PRP-002 test (6.5 #11). Test code only | The test takes `now` only after the batch's apply time is fixed; every assertion is kept; the test passes 20 times in a row locally (say how you ran it in the PR description); no file under `src/` changes in this commit |
| T1 | `ExportCsv` in Core: header, row line, amount format, empty `effective_to` and `currency` | Unit tests for every rule of the file format in 3.5: header text, LF endings, each amount case (`50000.0000`, `52000.5000`, `0.0001`, `0`, the largest PRP-001 amount), dates, empty fields, and that no line contains a CR |
| T2 | `ExportQuery`: fix the instant (transaction 1), stream the rows (transaction 2) | Integration tests: the rows equal the as-of read for every employee (3.4 item 2) on data with corrections and back-dated values; ordinal order (A2); the known-at boundary (6.5 #9); the lock is not held while rows are read (an upload of the same tenant commits while a reader is open) |
| T3 | The two endpoints: parameters, errors, headers, streaming, abort after the first byte, logging | Integration tests reproduce A1 to A8, every `400` and `404` case of 3.2 and 3.3 in the order of 3.3, one log entry per export with both outcomes, and no log entry for refused exports; all existing tests pass |
| T4 | Measure | The PR description gives local numbers for 3.6: the 1M-row export time, `VmHWM` before and after it, and the upload time during an open export. A commit for this task may be empty; put the numbers in its message too |

## 8. Validation loop

```bash
dotnet build CompLedger.slnx -warnaserror
dotnet format CompLedger.slnx --verify-no-changes
dotnet test tests/CompLedger.UnitTests
dotnet test tests/CompLedger.IntegrationTests
```

All PRP-001, PRP-002, PRP-003 and PRP-005 tests must keep passing; only the A8 test changes, in T0. Never weaken, skip or delete a test to make a level pass. If this PRP looks wrong or contradicts an earlier PRP, stop and say so in the PR description.

## 9. Definition of done

- [ ] Every rule in section 3 is implemented and covered by a test; A1 to A8 pass as tests.
- [ ] All validation levels are green on a clean checkout.
- [ ] No existing test method changes, except the PRP-002 A8 test in T0, whose assertions all stay.
- [ ] No migration is added and migrations `0001` to `0007` are untouched.
- [ ] No secrets, hostnames or credentials in the repository.
- [ ] Nothing under `specs/`, `.claude/`, `.github/` or `metrics/` was changed.
- [ ] The PR description maps each section-3 rule to its test, pastes the validation summaries, says how T0 was repeated, and gives the T4 measurements.

## 10. Anti-patterns

- Building the file, or a list of its rows, in memory; sorting in .NET.
- Streaming inside the transaction that holds the shared lock.
- Reading current values without the known-at filter, even for an export without `knownAt`.
- `ORDER BY employee_id` without `COLLATE "C"`.
- Culture-dependent number or date formatting; exponent notation; trailing zeros.
- Quoting fields, writing CRLF, or adding a BOM.
- Answering `404` for a tenant with no values.
- Writing a ProblemDetails body, or an error line in the CSV, after the file has started.
- Paging through employees with one as-of query each.
- Fixing the A8 test by retrying it, loosening an assertion, or changing the ingestor.
- Adding endpoints, parameters, formats or MCP tools this PRP does not list.

## 11. How this PRP is checked beyond the agent's view

1. **Holdout scenarios** for 3.2 to 3.5 through the HTTP API only, with other data: exact file bytes, amount spellings, ordinal order with ids chosen to differ from locale order, `X-As-Of` and `X-Known-At`, the same file again with the returned instant, snapshot exports against snapshot reads, every `400` and `404` case and their order, isolation, and A7 through a client that stops reading.
2. **Load gate** for 3.6: the 1M-row export time, `VmHWM` before and after it, and an upload during an open export; the existing budgets.
3. **AI review against this PRP** on the pull request.
4. All PRP-001, PRP-002, PRP-003 and PRP-005 holdout scenarios still run and must still pass.
5. **Not checked by the holdout scenarios:** A8 (a black-box client cannot see the ingestor's lock), the abort after the first byte (the scenarios cannot make the service fail mid-file), and the `Aborted` log entry of an export its client cancels (from outside, a client cannot tell how much of the file had already left the server into socket and proxy buffers, so the outcome is not fixed). They rest on the implementer's tests and review. The scenarios check `Completed` entries only.

## Changelog

| Version | Date       | Change |
|---------|------------|--------|
| 1.0     | 2026-11-15 | First version (Draft). Owner's decisions: CSV only; the export is the as-of view of all employees, now, as known at an instant, or through a snapshot; no computed fields in this PRP; the agent fixes the PRP-002 A8 test race as task T0 |
| 1.1     | 2026-11-15 | Closes the 7 gaps found while writing the holdout scenarios. The throughput budget names an export that exists: on the PRP-002 3.6 tenant only one of each employee's two dated values per component is in effect on a date, so the export has about half of the 1M values (500,001 rows as of 2025-08-01); still 30 s. Export memory is measured in a restarted API process after a small warm-up export, because `VmHWM` would otherwise still hold the 1M-row upload's peak (3.6). The upload-during-export check names the PRP-001 100k-row tenant (3.6). A wrong method on `/api/v1/snapshots/{anything}/export` is `405`, also with a malformed id (3.3). No `Content-Length` for any size (3.5). The `Aborted` entry of a cancelled export is not checked from outside (11.5). The default `asOf` is the UTC date of the request, as in PRP-001 (3.2) |
