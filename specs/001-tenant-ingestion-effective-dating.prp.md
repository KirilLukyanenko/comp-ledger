# PRP-001: Multi-tenant compensation ingestion with effective dating

| Field      | Value |
|------------|-------|
| Status     | Draft |
| Version    | 1.2 |
| Owner      | KirilLukyanenko |
| Created    | 2026-11-12 |
| Depends on | none (first feature; includes the solution skeleton) |
| Unlocks    | PRP-002 snapshots and as-known-at queries, PRP-003 computed fields, PRP-004 streaming export |

---

## 1. Goal

A tenant uploads a CSV file of compensation values (employee, component, effective date, amount, currency).
The service validates the whole file while streaming it, then applies it to PostgreSQL as one atomic batch, or rejects it with a precise error report.
After that, the API answers two questions for any employee in that tenant: "what was in effect on date D?" and "what is the full timeline?".
Tenants never see each other's data, and this is enforced by the database, not only by application code.

## 2. Why

- **Business.** Compensation platforms receive periodic feeds from each customer's HR system. Values change on effective dates, and back-dated corrections are common: a raise approved in May is effective from April 1. The system has to place each value on the right date and keep the old value when something is corrected, because auditors ask what was known and when.
- **Engineering.** This is the base table every later feature reads: snapshots (PRP-002), computed fields (PRP-003), streaming export (PRP-004). If ingestion is slow, lossy or leaks across tenants, everything built on it inherits the problem.
- **Scale.** A large tenant sends hundreds of thousands to millions of rows per feed. Ingestion must stream: memory must not grow with file size, and rows must not be inserted one by one.

## 3. What

### 3.1 Concepts

- **Tenant**: a customer. Every tenant-owned row carries `tenant_id`. Tenants are identified by a UUID.
- **Compensation value**: `(employee_id, component, effective_from, amount, currency)` within a tenant.
- **Key**: `(tenant, employee_id, component, effective_from)`. At most one *current* value per key.
- **Effective dating**: a value is in effect from `effective_from` (inclusive) until the day before the next later `effective_from` for the same `(employee_id, component)`, or open-ended if there is none. Dates are calendar dates with no time zone. "Today" means the UTC date at request time.
- **Correction**: a new value for an existing key with a different amount or currency. The old value is kept and marked superseded, never updated in place or deleted.
- **Batch**: one upload. A batch is either `Applied` (every row accepted) or `Rejected` (nothing applied). Both outcomes are recorded.

### 3.2 Component catalogue (fixed in this PRP)

| Code               | Meaning                          | Currency      | Amount range            |
|--------------------|----------------------------------|---------------|-------------------------|
| `BASE_SALARY`      | Annual base salary               | required      | 0 to 999999999999999.9999 |
| `ALLOWANCE`        | Annual fixed allowance           | required      | 0 to 999999999999999.9999 |
| `BONUS_TARGET_PCT` | Target bonus, percent of base    | must be empty | 0 to 500                |

Tenant-configurable components are out of scope (see section 5).

### 3.3 CSV format

- **Encoding.** UTF-8, BOM allowed. Bytes that are not valid UTF-8 are a file-level `INVALID_ENCODING` error, and reading stops there.
- **Syntax.** Comma delimiter, RFC 4180 quoting. A quoted field may contain commas, doubled quotes and line breaks.
- **Header.** The first record is always the header; there is no headerless mode. A file that starts with data gets header errors.
- **Columns.** Required, in any order: `employee_id, component, effective_from, amount, currency`. Header names are trimmed with the same whitespace set as values (see Trimming) and matched case-insensitively. Any other column is an error. Errors always name columns by their canonical lowercase name, except `UNKNOWN_COLUMN` (see 3.4).
- **Trimming.** After unquoting, leading and trailing whitespace (space, tab, CR, LF) is removed from every value, quoted or not. An empty `currency` means "no currency".
- **Row numbers** count records, not physical lines. The header is row 1 and the first data record is row 2 (what a user sees in a spreadsheet). A record whose quoted field spans several lines is one row.
- **Blank lines.** Empty or whitespace-only lines after the last data record are ignored, however many there are. A blank line anywhere before the last data record is a row, and it gets `MALFORMED_CSV`. A record with exactly one field that is empty after unquoting and trimming (for example a line holding only `""`) counts as a blank line.
- **Patterns** below are matched against the whole trimmed value and use ASCII characters only: `[0-9]` means the digits 0 to 9, not other Unicode digits. In .NET, use explicit character classes and the anchors `\A` and `\z`: never `\d`, and never `$` (it also matches before a final line break).

Field rules:

| Column           | Rule |
|------------------|------|
| `employee_id`    | `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`: 1 to 64 chars, starting with a letter or digit (so `.` and `..` are invalid), case-sensitive |
| `component`      | exact code from 3.2, case-sensitive |
| `effective_from` | `^[0-9]{4}-[0-9]{2}-[0-9]{2}$` and a real calendar date, otherwise `INVALID_DATE` (so `0000-01-01` and `2026-02-30` are `INVALID_DATE`). Then from 1900-01-01 to 2999-12-31, otherwise `DATE_OUT_OF_RANGE` |
| `amount`         | `^[0-9]{1,15}(\.[0-9]{1,4})?$`: dot as decimal separator; no sign, grouping or exponent. More than 4 decimals is an error, never rounded. Then within the component's range. For the money components the format already caps the value, so `AMOUNT_OUT_OF_RANGE` can only happen for `BONUS_TARGET_PCT` |
| `currency`       | First, presence must match the component (3.2). Then, when present and allowed, `^[A-Z]{3}$`. Validation against the ISO 4217 list is out of scope |

### 3.4 Error codes

| Code                    | Level  | When |
|-------------------------|--------|------|
| `EMPTY_FILE`            | file   | Nothing but a BOM and whitespace, or a valid header with no data rows |
| `INVALID_ENCODING`      | file   | Bytes that are not valid UTF-8. Reading stops at that point |
| `MISSING_COLUMN`        | header | A required column is absent. One error per missing column |
| `UNKNOWN_COLUMN`        | header | A column not in the list. One error per occurrence, so `grade,grade` gives two |
| `DUPLICATE_COLUMN`      | header | A required column appears more than once. One error per extra occurrence |
| `MALFORMED_CSV`         | row    | The row cannot be parsed, has the wrong number of fields, or is a blank line (3.3). Such a row gets no other errors |
| `INVALID_EMPLOYEE_ID`   | field  | Breaks the `employee_id` rule |
| `UNKNOWN_COMPONENT`     | field  | Not in the catalogue |
| `INVALID_DATE`          | field  | Not `yyyy-MM-dd` or not a real date |
| `DATE_OUT_OF_RANGE`     | field  | Outside 1900-01-01 to 2999-12-31 |
| `INVALID_AMOUNT`        | field  | Breaks the amount format |
| `AMOUNT_OUT_OF_RANGE`   | field  | Outside the component's range |
| `CURRENCY_REQUIRED`     | field  | Component needs a currency and it is empty |
| `CURRENCY_NOT_ALLOWED`  | field  | Component takes no currency and one is given |
| `INVALID_CURRENCY`      | field  | A currency is present and allowed, but it is not three uppercase ASCII letters |
| `DUPLICATE_KEY_IN_FILE` | row    | Two or more rows **without other errors** share `(employee_id, component, effective_from)`. Every row of the group gets this error. Rows that already have errors are not part of any group |

Error object: `{ "row": 4, "column": "amount", "code": "INVALID_AMOUNT", "message": "..." }`. `message` is free text and not part of the contract.

| Error level | `row` | `column` |
|-------------|-------|----------|
| file (`EMPTY_FILE`, `INVALID_ENCODING`) | `null` | `null` |
| header | `1` | `MISSING_COLUMN`, `DUPLICATE_COLUMN`: the canonical name. `UNKNOWN_COLUMN`: the name as written in the file, trimmed |
| row (`MALFORMED_CSV`, `DUPLICATE_KEY_IN_FILE`) | the row number | `null` |
| field | the row number | the canonical name |

Rules:

- **At most one error per field.** Checks run in the order the field rules list them, and the first failure wins. For example, `BONUS_TARGET_PCT` with currency `eur` gives only `CURRENCY_NOT_ALLOWED`. A row can still have errors in several fields.
- **Unknown component.** If a row's component is unknown, the checks that depend on the component (currency presence, amount range) are skipped for that row. Checks that don't depend on it (`employee_id`, date, amount format, currency format when a currency is present) still run.
- **Header errors stop processing.** If the header has any error, no data rows are read. The batch contains only the header errors, `rows.total` is 0, and `EMPTY_FILE` is not added.
- **`INVALID_ENCODING` stops reading.** Errors already found for fully parsed rows are kept. `rows.total` counts the data rows that were fully parsed before decoding failed. Decoding works on buffers, so this can be fewer than the rows that physically come before the bad bytes; it is never more.
- **Order.** By `row` ascending, with `null` first. Within row 1: header errors by the column's position in the file, then `MISSING_COLUMN` errors in canonical column order. Within a data row: the row-level error first, then field errors in canonical column order (`employee_id`, `component`, `effective_from`, `amount`, `currency`).
- **Cap.** The response and the stored batch contain the first 100 errors in that order. `errorCount` is the full total.

### 3.5 Ingestion semantics

1. **Atomic.** If any error exists, nothing is applied and the batch is recorded as `Rejected` with its errors.
2. **Classification** of each accepted row against the tenant's current values:
   - `inserted`: no current value for the key.
   - `unchanged`: a current value exists with the same amount (compared numerically, so `50000` equals `50000.00`) and the same currency. Nothing is written.
   - `corrected`: a current value exists with a different amount or currency. The old value is marked superseded by this batch, and the new value is inserted.
3. **Idempotent by content.** Uploading the same file twice gives `unchanged = total` on the second upload and changes nothing.
4. **Concurrency.** Batches of different tenants run in parallel. Batches of the same tenant are applied one at a time. The lock is taken only after the file has been fully read and staged, never while reading the request body.
5. **History is kept.** Superseded values stay in the table with `superseded_at` and `superseded_by_batch_id`. The API in this PRP only returns current values; querying what was known at a past moment is PRP-002.

### 3.6 API contract

Base path `/api/v1`. JSON uses camelCase. Dates are `"yyyy-MM-dd"`. Amounts are JSON numbers; their scale is not part of the contract, so clients compare numerically.

**Tenant resolution (temporary).** Authentication is out of scope. In this PRP the tenant comes from the `X-Tenant-Id` header. This header is a stand-in for a real identity later. The rule that matters now: no tenant-owned data can be read or written without a tenant context, and PostgreSQL row-level security enforces it.

- The value must be a UUID in the hyphenated `8-4-4-4-12` form; hex digits are case-insensitive.
- Anything else gives `401`: a missing or empty header, braces, no hyphens, any other text, the nil UUID, or a UUID that is not a known tenant.
- `/health` and `POST /api/v1/tenants` ignore the header completely, even when it is malformed.
- HTTP itself strips whitespace around header values, so surrounding whitespace is not part of this contract and is not checked.

| Method | Path | Tenant | Result |
|--------|------|--------|--------|
| GET  | `/health` | no | `200 {"status":"ok"}`. Liveness only: it doesn't touch the database |
| POST | `/api/v1/tenants` | no | Body `{"name":"Acme"}` with `Content-Type: application/json` gives `201 {"id","name"}`, with no `Location` (there is no tenant resource to point to yet). Another content type, or no `Content-Type`, gives `415`. Malformed JSON gives `400`. So does a `name` that is missing, `null` or not a string, or that is not 1 to 200 characters long after trimming (counted as .NET `string.Length`, i.e. UTF-16 code units). The name is stored and returned trimmed. Unknown JSON properties are ignored. Available only when `TenantAdminApi:Enabled=true`, otherwise `404` |
| POST | `/api/v1/ingestions` | yes | Body is CSV (content type rules below). Returns `201` with a batch (status `Applied`) and `Location: /api/v1/ingestions/{batchId}`, or `422` with a batch (status `Rejected`) and no `Location`. A body over `Ingestion:MaxBodyBytes` (default 256 MiB) gives `413` |
| GET  | `/api/v1/ingestions/{batchId}` | yes | `200` batch. `404` if it does not exist **in this tenant**, or if `batchId` is not a UUID |
| GET  | `/api/v1/employees/{employeeId}/compensation?asOf=yyyy-MM-dd` | yes | Values in effect on `asOf`, ordered by component code (ordinal). `asOf` is optional and defaults to today (UTC). When present, it must be a real `yyyy-MM-dd` date from 1900-01-01 to 2999-12-31, otherwise `400`. The value is not trimmed, so `asOf=%202031-01-01` is `400`, and an empty `asOf=` is also `400`. `404` if the employee has no values in this tenant at all; `200` with empty `components` if they have values but none in effect on that date |
| GET  | `/api/v1/employees/{employeeId}/compensation/history?component=CODE` | yes | All current periods, ordered by component, then `effectiveFrom`. `component` is optional and must be an exact, case-sensitive code from 3.2; an empty or unknown value gives `400`. A known code with no values for this employee gives `200` with empty `periods`. `404` only if the employee has no values at all |

**Request checks for `POST /api/v1/ingestions`**, in this order; the first one that fails decides the response:

1. Tenant: `401`.
2. Content type: `415` unless the media type is `text/csv` (compared case-insensitively). Parameters are allowed, but a `charset` other than `utf-8` (case-insensitive) gives `415`. A missing `Content-Type` gives `415`. Prefix matches such as `text/csv-schema` give `415`.
3. Declared size: a `Content-Length` over the limit gives `413` before any of the body is read.
4. Streamed size: a chunked body that goes over the limit while it is being read gives `413` on a best-effort basis. The server may close the connection, so the client may see a reset instead.

A batch is recorded only for `201` and `422`. A request refused with `401`, `413` or `415` leaves no batch and changes nothing.

**Order of checks on GET endpoints:** tenant (`401`), then query-parameter validation (`400`), then the resource lookup (`404`).

**Unknown routes** give `404` with `ProblemDetails`, whether or not `X-Tenant-Id` is present or valid. Tenant resolution only runs for the tenant endpoints in the table.

Batch resource:

```json
{
  "batchId": "0193f1c2-...",
  "status": "Applied",
  "receivedAt": "2026-11-12T09:15:02.123Z",
  "completedAt": "2026-11-12T09:15:03.456Z",
  "rows": { "total": 3, "inserted": 2, "corrected": 1, "unchanged": 0 },
  "errorCount": 0,
  "errors": []
}
```

For a rejected batch, `rows.total` is the number of data rows read, and `inserted`, `corrected` and `unchanged` are 0.

As-of response (`effectiveTo` is inclusive: the day before the next value's `effectiveFrom`, or `null`):

```json
{
  "employeeId": "E-1001",
  "asOf": "2026-05-15",
  "components": [
    { "component": "BASE_SALARY", "amount": 52000, "currency": "EUR",
      "effectiveFrom": "2026-04-01", "effectiveTo": "2026-06-30" },
    { "component": "BONUS_TARGET_PCT", "amount": 10, "currency": null,
      "effectiveFrom": "2026-01-01", "effectiveTo": null }
  ]
}
```

History response: `{ "employeeId": "...", "periods": [ { "component", "amount", "currency", "effectiveFrom", "effectiveTo" } ] }`.

Every error response uses RFC 7807 `ProblemDetails`, including `404` for unknown routes and for the disabled tenant admin route. There are two exceptions: the `422` rejected batch, whose body is the batch, and a streamed `413` where the connection is closed.

### 3.7 Non-functional budgets

These are starting budgets. The first CI run calibrates them, and any change is recorded in this PRP's changelog together with the measured numbers.

| Budget | Target |
|--------|--------|
| Throughput | 1,000,000-row file ingested end to end (HTTP upload until `201`) in 60 s or less on a GitHub-hosted `ubuntu-latest` runner, PostgreSQL 17 in Testcontainers with default settings. The budget is per row count; the gate's generator averages about 45 bytes per row (about 45 MB for 1M rows) |
| Memory | The metric is the API process's peak resident set size: `VmHWM` in `/proc/<pid>/status`, which .NET reports as `Process.PeakWorkingSet64`. Each file size runs in a fresh container after the same one-row warm-up upload. Peak at 1M rows: 300 MiB or less. Peak at 1M rows minus peak at 100k rows: 64 MiB or less. The second check is the real test of streaming |
| Logging | Console logs in JSON (`Logging:Console:FormatterName=json`). Exactly one entry per batch, with these message-template properties: `TenantId`, `BatchId`, `Status`, `TotalRows`, `InsertedRows`, `CorrectedRows`, `UnchangedRows`, `ErrorCount`, `DurationMs`. They appear under `State`, where the .NET JSON console formatter puts them. Types: the ids are lowercase hyphenated UUID strings, `Status` is `"Applied"` or `"Rejected"`, and the counts and `DurationMs` (whole milliseconds) are integers. **Never** log row contents or amounts: salary data is personal data |

## 4. Acceptance examples (public)

A separate holdout suite, which is not in this repository, checks the same rules through the HTTP API with different data and more edge cases. Passing these examples is necessary but not sufficient. **Implement the rules in section 3, not these examples.**

In all examples, tenants `T1` and `T2` are created through `POST /api/v1/tenants`, and the CSV header is `employee_id,component,effective_from,amount,currency`.

**A1. Basic ingest and as-of.**
Given T1 uploads:
```
E-1001,BASE_SALARY,2026-01-01,50000,EUR
E-1001,BASE_SALARY,2026-07-01,55000,EUR
E-1001,BONUS_TARGET_PCT,2026-01-01,10,
```
Then the response is `201` with `rows = {total:3, inserted:3, corrected:0, unchanged:0}`.
As of `2026-03-15`: `BASE_SALARY` is 50000 EUR, from `2026-01-01` to `2026-06-30`; `BONUS_TARGET_PCT` is 10 with currency `null` and `effectiveTo` `null`.
As of `2026-07-01`: `BASE_SALARY` is 55000 with `effectiveTo` `null`.
As of `2025-12-31`: `200` with empty `components`.

**A2. A back-dated value splits a period.**
Given A1, T1 uploads `E-1001,BASE_SALARY,2026-04-01,52000,EUR`. Then `inserted = 1`, and the `BASE_SALARY` history is
`[2026-01-01 to 2026-03-31: 50000]`, `[2026-04-01 to 2026-06-30: 52000]`, `[2026-07-01 to null: 55000]`.

**A3. Re-upload is a no-op.**
Given A1, T1 uploads the A1 file again, with `50000` written as `50000.00`. Then `201` with `inserted = 0, corrected = 0, unchanged = 3`, and the history is unchanged.

**A4. Correction.**
Given A1, T1 uploads `E-1001,BASE_SALARY,2026-01-01,51000,EUR`. Then `corrected = 1`, and as of `2026-02-01` the value is 51000.
In the database, the 50000 row still exists with `superseded_at` set (checked by integration tests, not by the API).

**A5. One bad row rejects the whole file.**
T1 uploads:
```
E-2001,BASE_SALARY,2026-01-01,40000,EUR
E-2002,BASE_SALARY,2026-01-01,41000,EUR
E-2003,BASE_SALARY,2026-01-01,"5,000",EUR
```
Then `422` with one error `{row: 4, column: "amount", code: "INVALID_AMOUNT"}`. `E-2001` and `E-2002` return `404`. `GET /ingestions/{batchId}` shows `Rejected` with `errorCount: 1`.

**A6. Duplicate key in the file.**
Rows 2 and 5 share `E-3001,BASE_SALARY,2026-01-01` with different amounts. Then `422` with `DUPLICATE_KEY_IN_FILE` on row 2 and on row 5.

**A7. Currency rules per component.**
`E-4001,BONUS_TARGET_PCT,2026-01-01,15,EUR` gives `CURRENCY_NOT_ALLOWED` (column `currency`).
`E-4001,BASE_SALARY,2026-01-01,45000,` gives `CURRENCY_REQUIRED`.

**A8. Tenant isolation.**
Given A1 in T1:
T2 `GET /employees/E-1001/compensation` returns `404`.
T2 `GET /ingestions/{T1 batch id}` returns `404`.
T2 uploads `E-1001,BASE_SALARY,2026-01-01,70000,USD` and gets `inserted = 1` (not `corrected`).
T1 still sees 50000 EUR as of `2026-03-15`.

**A9. Tenant resolution.**
A missing `X-Tenant-Id`, a value that is not a UUID, and an unknown UUID all return `401`.

**A10. Header and transport errors.**
A file without a `currency` column returns `422` with `MISSING_COLUMN` on row 1, and no data rows are validated.
`Content-Type: application/json` returns `415`.

## 5. Out of scope

- Authentication and authorization (a later PRP replaces `X-Tenant-Id`).
- Deleting or end-dating a component (for example, an allowance that stops). Open question for a later PRP.
- Tenant-configurable component catalogues.
- Asynchronous or event-driven ingestion through a queue (later PRP, RabbitMQ).
- Snapshots and as-known-at queries (PRP-002), computed fields (PRP-003), export (PRP-004).
- Checking currency codes against the ISO 4217 list.
- UI.

## 6. Context for the agent

### 6.1 Read first

- `CLAUDE.md` (repository rules, commit convention, what you may not edit)
- `specs/README.md` (how PRPs are used here)
- Npgsql COPY: https://www.npgsql.org/doc/copy.html
- PostgreSQL row-level security: https://www.postgresql.org/docs/current/ddl-rowsecurity.html
- Testcontainers for .NET, PostgreSQL module: https://dotnet.testcontainers.org/modules/postgres/
- CsvHelper: https://joshclose.github.io/CsvHelper/
- DbUp: https://github.com/DbUp/DbUp

### 6.2 Current state of the repository

```
comp-ledger/
├── CLAUDE.md
├── README.md
├── .claude/            # skills and sub-agents (do not edit)
├── .github/workflows/  # CI (do not edit)
└── specs/              # PRPs (do not edit)
```

### 6.3 Target tree after this PRP

```
comp-ledger/
├── CompLedger.slnx
├── Directory.Build.props          # net10.0, Nullable, TreatWarningsAsErrors
├── Directory.Packages.props       # central package versions
├── Dockerfile                     # API image, listens on 8080
├── db/migrations/
│   ├── 0001_tenants.sql
│   ├── 0002_ingestion_batches.sql
│   ├── 0003_compensation_values.sql
│   └── 0004_row_level_security.sql
├── src/
│   ├── CompLedger.Core/           # pure code, no I/O
│   │   ├── Components.cs          # catalogue from 3.2
│   │   └── Ingestion/
│   │       ├── CompensationRow.cs
│   │       ├── RowParser.cs       # header and record validation, error codes from 3.4
│   │       └── ErrorCollector.cs  # keeps the first 100 errors, counts all
│   ├── CompLedger.Infrastructure/
│   │   ├── Db/Migrator.cs         # DbUp, embedded scripts from db/migrations
│   │   ├── Db/TenantScope.cs      # opens a transaction and sets app.tenant_id
│   │   ├── Ingestion/PostgresIngestor.cs
│   │   └── Queries/CompensationQueries.cs
│   └── CompLedger.Api/
│       ├── Program.cs
│       ├── Tenancy/TenantResolutionMiddleware.cs
│       └── Endpoints/{Health,Tenant,Ingestion,Compensation}Endpoints.cs
└── tests/
    ├── CompLedger.UnitTests/          # RowParser, ErrorCollector, catalogue rules
    └── CompLedger.IntegrationTests/   # Testcontainers PostgreSQL + WebApplicationFactory
```

### 6.4 Technical decisions (fixed: changing one requires a PRP update first)

| Decision | Reason |
|----------|--------|
| .NET 10, ASP.NET Core Minimal APIs, `.slnx` solution | Current LTS; minimal surface |
| PostgreSQL 17, Npgsql, Dapper for queries, Npgsql binary `COPY` for bulk writes. **No EF Core in this PRP** | The bulk path and row-level security both need raw SQL. One data-access style keeps generated code consistent |
| Migrations as plain `.sql` files applied by DbUp (`dbup-postgresql`) | Row-level security policies and grants are SQL anyway; plain files are easy to review |
| Shared schema, `tenant_id` on every tenant-owned table, plus PostgreSQL row-level security | The database is the last line of defense if application code forgets a filter |
| CsvHelper with `ReadAsync` | Mature async streaming API that models know well, so fewer invented APIs |
| UUIDv7 (`Guid.CreateVersion7()`) for tenant and batch ids | Ordered ids, good index locality |
| xUnit v3 with the built-in `Assert`; Testcontainers.PostgreSql; `postgres:17-alpine` | FluentAssertions 8 has a commercial license, so it is not used |
| Database roles: `comp_app` (NOLOGIN) holds table privileges; the app connects with a LOGIN role that is a member of `comp_app` | The app never connects as table owner or superuser, so row-level security applies |

**Role provisioning is the environment's job, not the migrations' job.** Before the first start, the environment creates `comp_app` (NOLOGIN) and a LOGIN role in it with a generated password. The integration-test fixture and the holdout harness both do this. Migrations grant privileges to `comp_app` and fail with a clear message if it does not exist. **No password appears anywhere in the repository.**

Runtime configuration (environment variables):
`ConnectionStrings__App`, `ConnectionStrings__Migrations`, `Database__MigrateOnStartup` (`true`/`false`), `TenantAdminApi__Enabled`, `Ingestion__MaxBodyBytes`. Nothing else is required; `ASPNETCORE_ENVIRONMENT` may be left unset (Production).

- `ConnectionStrings__Migrations` connects as the role that owns the schema. In tests this is the container's superuser.
- `ConnectionStrings__App` connects as a LOGIN role that is a member of `comp_app`, with no superuser and no `BYPASSRLS`. Its name is not fixed; the test fixture and the holdout harness both use `comp_app_login`.

Container contract:

- The image is built from `mcr.microsoft.com/dotnet/aspnet:10.0` and listens on port 8080. The base image includes a shell and `cat`, which the load gate needs to read `/proc/1/status`. Don't switch to a chiseled or distroless image.
- The API process must be PID 1 in the container. Use an exec-form `ENTRYPOINT ["dotnet", "CompLedger.Api.dll"]`, with no shell wrapper; the load gate reads `/proc/1/status`.
- With `Database__MigrateOnStartup=true`, migrations finish before the app starts listening, so a `200` from `/health` means the schema is in place.

### 6.5 Known gotchas

1. **Superusers bypass row-level security**, even with `FORCE ROW LEVEL SECURITY`. The Testcontainers default user `postgres` is a superuser. Tests must call the API through the non-superuser app login, or isolation tests pass for the wrong reason. Also set `FORCE ROW LEVEL SECURITY` so the table owner does not bypass the policies.
2. **Use `set_config('app.tenant_id', @t, true)` inside a transaction.** The third argument makes it transaction-local. A session-level `SET` leaks between tenants through the connection pool.
3. **An unset custom setting may be `''`, not `NULL`.** After a transaction-local `set_config`, the session value goes back to an empty string. Use `nullif(current_setting('app.tenant_id', true), '')::uuid` in policies, otherwise `''::uuid` throws. With no tenant set, policies must return no rows (fail closed).
4. **Npgsql binary import needs `CompleteAsync()`.** Disposing the importer without it cancels the import silently. Write with explicit types: `DateOnly` as `NpgsqlDbType.Date`, `decimal` as `Numeric`, `string` as `Text`; write an empty currency with `WriteNullAsync()`.
5. **Temp tables are not analyzed automatically.** Run `ANALYZE staging` after `COPY`, otherwise the planner guesses row counts and the merge can pick a bad plan at 1M rows. Create the staging table with `ON COMMIT DROP`.
6. **Kestrel limits request bodies to about 30 MB by default.** Raise the limit on the ingestion endpoint only (`RequestSizeLimitAttribute` metadata, value from `Ingestion:MaxBodyBytes`). Read `HttpRequest.Body` as a stream. Do not use `IFormFile`, `[FromBody] string` or anything that buffers the body.
7. **Synchronous I/O on the request body is disabled.** Use `CsvReader.ReadAsync()` and async Npgsql calls only.
8. **Parse with invariant culture.** Check the amount against the regex first, then `decimal.Parse(..., CultureInfo.InvariantCulture)`. Parse dates with `DateOnly.TryParseExact("yyyy-MM-dd", InvariantCulture)`.
9. **Take the advisory lock after staging, not before.** Otherwise one slow upload blocks every other upload of that tenant while bytes are still arriving.
10. **Compare numerically, not as strings.** `numeric` equality makes `50000 = 50000.0000` true. Compare currency with `IS DISTINCT FROM` because it can be `NULL`.
11. **A default `StreamReader` hides bad bytes.** It replaces invalid UTF-8 with U+FFFD and carries on. Use `new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)` and turn the `DecoderFallbackException` into `INVALID_ENCODING`.
12. **.NET regex traps.** `\d` matches any Unicode digit (Arabic-Indic, fullwidth), and `decimal.Parse` then fails with a 500. `$` also matches before a final `\n`. Use `[0-9]` with `\A`…`\z`.
13. **Kestrel's own 404 and 413 have no body.** Use `AddProblemDetails()` with `UseStatusCodePages()` so unknown routes and size-limit errors also return `ProblemDetails`.

## 7. Implementation blueprint

### 7.1 Data model (normative)

```sql
CREATE TABLE tenants (
  id          uuid PRIMARY KEY,
  name        text NOT NULL,
  created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE ingestion_batches (
  id              uuid PRIMARY KEY,
  tenant_id       uuid NOT NULL REFERENCES tenants(id),
  status          text NOT NULL CHECK (status IN ('Applied', 'Rejected')),
  received_at     timestamptz NOT NULL,
  completed_at    timestamptz NOT NULL,
  total_rows      int NOT NULL,
  inserted_rows   int NOT NULL,
  corrected_rows  int NOT NULL,
  unchanged_rows  int NOT NULL,
  error_count     int NOT NULL,
  errors          jsonb NOT NULL DEFAULT '[]'   -- at most 100 entries
);

CREATE TABLE compensation_values (
  id                      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  tenant_id               uuid NOT NULL REFERENCES tenants(id),
  employee_id             text NOT NULL,
  component               text NOT NULL,
  effective_from          date NOT NULL,
  amount                  numeric(19,4) NOT NULL,
  currency                char(3),
  batch_id                uuid NOT NULL REFERENCES ingestion_batches(id),
  recorded_at             timestamptz NOT NULL,
  superseded_at           timestamptz,
  superseded_by_batch_id  uuid REFERENCES ingestion_batches(id)
);

-- one current value per key
CREATE UNIQUE INDEX ux_compensation_current
  ON compensation_values (tenant_id, employee_id, component, effective_from)
  WHERE superseded_at IS NULL;
```

Row-level security: enable and force it on `ingestion_batches` and `compensation_values`, with a policy `USING` and `WITH CHECK` of `tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid`.
`tenants` has no policy: the app role gets `SELECT, INSERT` on it only.

### 7.2 Ingestion pipeline

```
POST /api/v1/ingestions
  tenant  <- TenantContext (middleware already returned 401 if absent)
  check content type (415) and Content-Length (413); Kestrel enforces the limit while streaming as well
  batchId <- Guid.CreateVersion7(); receivedAt <- UtcNow

  open connection (app login); begin transaction
  SELECT set_config('app.tenant_id', tenant, true)
  CREATE TEMP TABLE staging (row_no int, employee_id text, component text,
                             effective_from date, amount numeric(19,4), currency char(3)) ON COMMIT DROP
  importer <- BeginBinaryImportAsync("COPY staging (...) FROM STDIN (FORMAT BINARY)")
  validate header -> on error: record it and stop reading rows
  for each record in csv.ReadAsync(request.Body):      // one record in memory at a time
      result <- RowParser.Parse(record, rowNo)
      if valid: write row to importer
      else:     errors.Add(result.Errors)             // keeps the first 100, counts all
  importer.CompleteAsync()
  ANALYZE staging
  duplicate errors <- SQL over staging grouped by key HAVING count(*) > 1
                      (total count + first 100 by row_no); merge with the other errors, keep the first 100 by (row, column)

  if any errors:
      rollback
      in a new transaction (tenant context set): insert a Rejected batch with counts and errors
      return 422 + batch

  SELECT pg_advisory_xact_lock(hashtextextended(tenant::text, 0))
  insert the batch row (Applied, counts 0)

  -- corrected: supersede current rows whose value differs
  UPDATE compensation_values c SET superseded_at = now(), superseded_by_batch_id = @batch
  FROM staging s
  WHERE c.tenant_id = @tenant AND c.superseded_at IS NULL
    AND (c.employee_id, c.component, c.effective_from) = (s.employee_id, s.component, s.effective_from)
    AND (c.amount <> s.amount OR c.currency IS DISTINCT FROM s.currency)
  corrected <- affected rows

  -- insert every staged row that now has no current value (new and corrected)
  INSERT INTO compensation_values (...) SELECT ... FROM staging s
  WHERE NOT EXISTS (current row with the same key)
  written <- affected rows
  inserted <- written - corrected;  unchanged <- total - written

  update the batch counts; set completed_at; commit
  log the batch line (no row contents); return 201 + Location + batch
```

As-of query: `DISTINCT ON (component)` over current rows with `effective_from <= @asOf`, ordered by `component, effective_from DESC`.
`effectiveTo` and the history come from `lead(effective_from) OVER (PARTITION BY component ORDER BY effective_from) - 1`.

### 7.3 Tasks (in order; one commit per task, message `PRP-001 Tn: <summary>`)

| #  | Task | Done when |
|----|------|-----------|
| T1 | Solution skeleton: `.slnx`, `Directory.Build.props`, `Directory.Packages.props`, three `src` projects, two test projects, `/health` | Level 1 is green |
| T2 | Migrations 0001 to 0004, DbUp migrator, integration fixture (Postgres container, role provisioning with a random password, migrations as owner, app connects as the app login) | Integration tests prove: with no tenant set, 0 rows are visible; tenant A cannot read or insert B's rows; the app login is not a superuser |
| T3 | `Components`, `RowParser`, `ErrorCollector` in Core | Unit tests cover every error code in 3.4 and every boundary in 3.3 (min and max dates, 4 vs 5 decimals, 15 vs 16 integer digits, percent at 500 vs 500.0001) |
| T4 | `PostgresIngestor`: staging, `COPY`, duplicate detection, merge, batch records | Integration tests cover inserted, unchanged, corrected and rejected, the superseded row kept in A4, and two concurrent batches for one tenant giving the same result as sequential ones |
| T5 | Endpoints, tenant middleware, queries, ProblemDetails | Integration tests through `WebApplicationFactory` reproduce A1 to A10 |
| T6 | `Dockerfile` and the runtime configuration from 6.4 | `docker build` succeeds; the container starts against a Testcontainers database and `/health` returns 200 |

## 8. Validation loop (run these yourself; all must be green before you open a PR)

```bash
# Level 1: build and style
dotnet build CompLedger.slnx -warnaserror
dotnet format CompLedger.slnx --verify-no-changes

# Level 2: unit tests
dotnet test tests/CompLedger.UnitTests

# Level 3: integration tests (needs Docker)
dotnet test tests/CompLedger.IntegrationTests
```

If a level fails, fix the code and rerun that level. Never weaken, skip or delete a test to make a level pass. If you believe a test or this PRP is wrong, stop and say so in the PR description.

## 9. Definition of done

- [ ] Every rule in section 3 is implemented; examples A1 to A10 pass as integration tests.
- [ ] Levels 1 to 3 are green on a clean checkout.
- [ ] No buffering of the request body, no row-by-row inserts, no session-level tenant setting.
- [ ] No secrets, passwords, hostnames or connection strings with credentials in the repository.
- [ ] Nothing under `specs/`, `.claude/` or `.github/` was changed.
- [ ] The PR description maps each section-3 rule to the test that covers it and pastes the Level 1 to 3 summaries.

## 10. Anti-patterns

- Loading the whole file into memory, a `List<CompensationRow>` of all rows, or `ReadToEndAsync()`.
- One `INSERT` per row, or EF Core `AddRange` for the bulk path.
- Filtering by `tenant_id` only in C# and skipping row-level security.
- Running integration tests as the `postgres` superuser.
- Logging CSV contents, employee ids together with amounts, or whole request bodies.
- Adding endpoints, columns or components that are not in this PRP.
- Catching exceptions to turn a failing test green.

## 11. How this PRP is checked beyond the agent's view

The implementing agent sees this PRP and the repository. It does not see:

1. **Holdout behaviour scenarios** in a separate repository. They run the API container against Testcontainers PostgreSQL and call it over HTTP only, with data that differs from section 4.
2. **The load gate** for section 3.7: 100k-row and 1M-row uploads, with wall time and peak resident set size measured.
3. **AI review against this PRP.** A reviewer agent receives the PRP and the diff, and must point to the code and test behind each section-3 rule, or report the rule as missing.

Recorded for every run: whether the first complete attempt passed all gates, how many human commits the PR needed before merge, and tokens, cost and wall time of the agent runs.

## Changelog

| Version | Date       | Change |
|---------|------------|--------|
| 1.0     | 2026-11-12 | First version |
| 1.1     | 2026-11-12 | Resolved the 45 gaps found while writing the holdout scenarios (still Draft, nothing implemented). 3.3: encoding error, header-first rule, trimming, record-based row numbers, blank lines, ASCII-only patterns, stricter `employee_id`, year 0000. 3.4: `INVALID_ENCODING`; `row` and `column` per error level; one error per field and check order; unknown-component behaviour; header errors stop processing; duplicates only among rows without errors; exact ordering. 3.6: `X-Tenant-Id` format; request-check order; content-type rules; no batch for 401/413/415; `Location` only on 201; `asOf` and `component` validation; component order; tenant creation validation; ProblemDetails everywhere. 3.7: memory metric is process peak RSS in MiB, fresh container per size; JSON log fields. 6.4: roles, base image, migrations before listening. 6.5: gotchas 11–13 |
| 1.2     | 2026-11-12 | Resolved the 10 gaps found while aligning the holdout scenarios with 1.1 (still Draft). 3.3: header names use the value trimming set; a lone `""` record is a blank line. 3.4: `rows.total` after `INVALID_ENCODING` counts fully parsed rows only. 3.6: header whitespace is not part of the contract; tenant creation details (trimmed name, length unit, null or non-string name, missing content type); `asOf` is not trimmed; GET check order 401 → 400 → 404; unknown routes are 404 without a tenant check. 3.7: where the log properties appear and their types. 6.4: the API runs as PID 1 |
