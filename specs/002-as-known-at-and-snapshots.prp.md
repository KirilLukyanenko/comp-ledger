# PRP-002: As-known-at queries and snapshots

| Field      | Value |
|------------|-------|
| Status     | Draft |
| Version    | 1.0 |
| Owner      | KirilLukyanenko |
| Created    | 2026-11-13 |
| Depends on | PRP-001 (2.1, implemented) |
| Unlocks    | PRP-003 computed fields as known at a time, PRP-004 export of a snapshot |

---

## 1. Goal

The API answers "what was in effect on date D, **as the system knew it at instant K**?" for any employee in a tenant, not only "as it knows it now". A tenant can also save a **snapshot**: a named pair of date and instant, for example "FY2026 review: values in effect on 2026-12-31, as known when the review started". The snapshot gives the same answer forever, whatever is uploaded or corrected later.

## 2. Why

- **Business.** Compensation decisions are made on the data known at the time. When a raise is back-dated or a value corrected after a review closed, auditors and HR ask "what did we see when we decided?", and the answer must not change afterwards. Review cycles freeze their input as a snapshot.
- **Engineering.** PRP-001 already keeps every value with the time it became known (`recorded_at`) and the time it was replaced (`superseded_at`), and never deletes or updates a value in place. That makes the data bitemporal: valid time (effective dates) and knowledge time. This PRP exposes the second axis. Because nothing is ever overwritten, a snapshot needs no copy of the data, only the two coordinates.
- **Scale.** A large tenant has millions of values. A snapshot must be created in constant time, and an as-known-at read for one employee must use an index, not a scan.

## 3. What

### 3.1 Concepts

- **Apply time.** Every `Applied` batch has one apply time (PRP-001 7.1 and 7.2): the moment the batch got its tenant's lock. All values the batch inserts have it as `recorded_at`; all values it supersedes have it as `superseded_at`. Within a tenant, apply times follow the order in which batches were applied. A `Rejected` batch has no apply time.
- **Known at K.** A value is known at instant K when `recorded_at <= K` and (`superseded_at` is null or `superseded_at > K`). Both bounds matter: at exactly a batch's apply time, its new values are known and the values it superseded are not. "Current" in PRP-001 is the same set, with K = now.
- **As-known-at query.** A PRP-001 as-of or history query computed over the values known at K instead of the current values. Periods (`effectiveTo`) are computed among the values known at K, so a value back-dated after K does not split a period as seen at K.
- **Snapshot.** A tenant-owned, named pair `(asOf, knownAt)`: a calendar date and an instant. Reading an employee through a snapshot is the as-known-at as-of query for that pair. A snapshot stores no compensation values.
- **Stable answer.** An answer for a given `(asOf, knownAt)` never changes later. 3.4 says how the service guarantees it.
- **Request time.** The database clock (`clock_timestamp()`) read while the request is served, after the wait in 3.4 item 4. It is the same clock that sets apply times, so an instant the service returned is never "in the future" for it.

### 3.2 Inputs and validation

**`knownAt`** (query parameter, and the snapshot's `knownAt`):

- Format: `^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6})?Z$` on the raw value, matched with `\A` and `\z` and ASCII classes only (PRP-001 3.3 Patterns). UTC only: the `T` and the `Z` are uppercase, and offsets such as `+01:00` are not accepted. At most 6 fractional digits (microseconds, PostgreSQL's precision).
- It must be a real instant (so `2026-02-30T00:00:00Z` and hour `24` are invalid), from `1900-01-01T00:00:00Z` up to the request time (3.1), inclusive. A later instant is invalid: a batch applied after the request could still get an earlier apply time, so the answer could change.
- Not trimmed. Empty (`knownAt=`) is invalid. Given more than once is invalid, even with equal values.
- Any violation gives `400`.

**`asOf`** keeps the PRP-001 rules (3.6) everywhere it appears, including the snapshot body, where it is required.

**Snapshot body** for `POST /api/v1/snapshots`, `Content-Type: application/json` with the same media-type rules as `POST /api/v1/tenants` (PRP-001 3.6):

```json
{ "name": "FY2026 review", "asOf": "2026-12-31" }
```

- `name`: required string, trimmed with the CSV whitespace set (space, tab, CR, LF), then 1 to 200 UTF-16 code units. A name containing U+0000 gives `400`. Stored and returned trimmed.
- `asOf`: required string, a real `yyyy-MM-dd` date from 1900-01-01 to 2999-12-31, not trimmed.
- Property names match case-insensitively; unknown properties are ignored; a missing, `null` or non-string `name` or `asOf` gives `400`; malformed JSON gives `400`.
- The client does not choose `knownAt`: the service sets it (3.4).
- Names are unique within a tenant, compared ordinally after trimming. A duplicate gives `409`. Two tenants may use the same name.

**Path parameters.**

- `snapshotId` must be a hyphenated UUID; anything else gives `404`, like `batchId` in PRP-001.
- `employeeId` in any employee path, the PRP-001 ones included, that contains U+0000 gives `404`: no such employee can exist (PRP-001 3.3), and PostgreSQL cannot take the value.

### 3.3 Error responses

Every error is RFC 7807 `ProblemDetails`, as in PRP-001.

| Status | When |
|--------|------|
| `400` | Invalid `knownAt`, `asOf`, `component` (PRP-001 rules), snapshot body or name; `asOf` or `knownAt` given on a snapshot read |
| `401` | Tenant resolution, exactly as in PRP-001 3.6 |
| `404` | Unknown or foreign snapshot, malformed `snapshotId`; employee with no values known at the requested instant (3.4, 5) |
| `405` | Known path, wrong method (PRP-001 3.6 rule) |
| `409` | Snapshot name already used in this tenant |
| `415` | `POST /api/v1/snapshots` content type |

Order of checks: tenant (`401`), then content type (`415`, POST only), then query and body validation (`400`), then lookups (`404`), then the name conflict (`409`).

### 3.4 Semantics

1. **Without `knownAt`, nothing changes.** The PRP-001 as-of and history endpoints answer exactly as before. Their responses gain one field, `knownAt`, which is `null` when the parameter was not given.
2. **With `knownAt`**, the as-of and history endpoints use the values known at `knownAt` (3.1). All PRP-001 rules about ordering, periods, `effectiveTo` and the `component` filter apply to that set.
3. **Existence is also as known.** With `knownAt`, `404` means the employee had no values known at `knownAt`. `200` with empty `components` or `periods` means they had values known at `knownAt`, but none in effect on `asOf` (or none for that component).
4. **Stable answers.** A request with `knownAt`, a snapshot read and a snapshot creation each **wait while a batch of the same tenant is being applied**: from the moment it takes its lock to its commit (PRP-001 3.5 item 4). They never wait while a batch is still being uploaded or validated. After the wait, every batch with an apply time at or before the instant has committed, and every later batch will get a later apply time, so the answer cannot change.
5. **Snapshot creation.** After the wait in item 4, the snapshot's `knownAt` is set to the database clock (`clock_timestamp()`), then the snapshot is stored. Creating a snapshot copies no values and takes the same time for any tenant size.
6. **Snapshot reads** are the as-known-at as-of query for the snapshot's `asOf` and `knownAt`. The read accepts no `asOf` or `knownAt` parameter: giving either is `400`.
7. **Snapshots are immutable.** There is no update or delete in this PRP.
8. **Rejected batches** have no apply time and never change any as-known-at answer.
9. **Tenant isolation.** Snapshots are tenant-owned and protected by row-level security like the PRP-001 tables. A snapshot of another tenant is `404`, and the list shows only the caller's snapshots.

### 3.5 API contract

Base path, JSON conventions and tenant resolution as in PRP-001 3.6.

| Method | Path | Tenant | Result |
|--------|------|--------|--------|
| GET  | `/api/v1/employees/{employeeId}/compensation?asOf=yyyy-MM-dd&knownAt=K` | yes | PRP-001 as-of response, plus `knownAt`. `knownAt` optional (3.4) |
| GET  | `/api/v1/employees/{employeeId}/compensation/history?component=CODE&knownAt=K` | yes | PRP-001 history response, plus `knownAt` |
| POST | `/api/v1/snapshots` | yes | `201` with the snapshot and `Location: /api/v1/snapshots/{snapshotId}`. `400`, `409`, `415` (3.3) |
| GET  | `/api/v1/snapshots` | yes | `200 {"snapshots": [...]}`, ordered by `knownAt`, then `snapshotId`. Empty list if none |
| GET  | `/api/v1/snapshots/{snapshotId}` | yes | `200` snapshot, or `404` |
| GET  | `/api/v1/snapshots/{snapshotId}/employees/{employeeId}/compensation` | yes | `200` as-of response at the snapshot's `asOf` and `knownAt`, plus `snapshotId`. `404` for an unknown snapshot, or an employee with no values known at its `knownAt` |

The PRP-001 batch resource gains `appliedAt`: the batch's apply time, or `null` for a `Rejected` batch.

**Instant format in responses** (`knownAt`, `appliedAt`): UTC with exactly six fractional digits, `"2026-11-13T14:22:31.123456Z"`. An instant returned by the service, sent back as `knownAt`, means exactly that instant. The PRP-001 fields `receivedAt` and `completedAt` keep their format.

Snapshot resource:

```json
{ "snapshotId": "0193f1c2-...", "name": "FY2026 review", "asOf": "2026-12-31", "knownAt": "2026-11-13T14:22:31.123456Z" }
```

As-of response with `knownAt` (snapshot reads add `"snapshotId"`):

```json
{
  "employeeId": "E-1001",
  "asOf": "2026-03-01",
  "knownAt": "2026-11-13T14:22:31.123456Z",
  "components": [
    { "component": "BASE_SALARY", "amount": 50000, "currency": "EUR", "effectiveFrom": "2026-01-01", "effectiveTo": null }
  ]
}
```

### 3.6 Non-functional budgets

Starting budgets. The first CI run calibrates them; any change is recorded in the changelog with the measured numbers.

| Budget | Target |
|--------|--------|
| Ingestion | PRP-001 3.7 budgets still hold with this PRP's new index: 1M rows in 60 s or less, the same memory budgets |
| As-known-at latency | After the PRP-001 load gate's 1M-row file is ingested into a tenant, then corrected by a second upload of 100k rows, 200 sequential as-of requests with `knownAt` for different employees: p95 of the HTTP round trip 100 ms or less on a GitHub-hosted runner |
| Snapshot creation | Independent of tenant size: with the 1M-row tenant above, `POST /api/v1/snapshots` p95 100 ms or less over 20 requests, when no batch is being applied |
| Logging | One JSON log entry per created snapshot with `TenantId`, `SnapshotId`, `AsOf` (`yyyy-MM-dd`), `KnownAt` (the response format) and `DurationMs`, under `State` as in PRP-001 3.7. Reads log nothing of their own. Never log names together with values, or any amount |

## 4. Acceptance examples (public)

A separate holdout suite checks the same rules through the HTTP API with different data and more edge cases. **Implement the rules in section 3, not these examples.** `t1`, `t2` are the `appliedAt` values of the batches named below, as returned by the service; `t2 - 1µs` is one microsecond earlier.

**A1. A correction, seen before and after.**
T1 uploads `E-1001,BASE_SALARY,2026-01-01,50000,EUR` (batch 1, `appliedAt` = t1), then `E-1001,BASE_SALARY,2026-01-01,51000,EUR` (batch 2, t2).
As of `2026-03-01`: with `knownAt=t1`, 50000; with `knownAt=t2 - 1µs`, 50000; with `knownAt=t2`, 51000; without `knownAt`, 51000 and `"knownAt": null`. With `knownAt` one microsecond before t1: `404`.

**A2. A back-dated value does not change the past view.**
Given A1, T1 uploads `E-1001,BASE_SALARY,2026-04-01,53000,EUR` (t3). The history with `knownAt=t2` is one period `[2026-01-01 to null: 51000]`. With `knownAt=t3` it is `[2026-01-01 to 2026-03-31: 51000]`, `[2026-04-01 to null: 53000]`.

**A3. A snapshot does not move.**
Given A1, T1 creates `{"name": "Q1 review", "asOf": "2026-03-31"}`: `201`, `Location` set, `knownAt` at or after t2. Then T1 uploads `E-1001,BASE_SALARY,2026-01-01,52000,EUR`. The snapshot read for `E-1001` still shows 51000; the as-of endpoint without `knownAt` shows 52000.

**A4. Snapshot names.**
Creating `"Q1 review"` again gives `409`, and so does `"  Q1 review "`. T2 can create `"Q1 review"`. `{"name": "Q1 review 2"}` without `asOf` gives `400`.

**A5. `knownAt` validation.**
Each gives `400`: `2026-11-13T10:00:00+01:00`, `2026-11-13t10:00:00Z`, `2026-11-13T10:00:00.1234567Z`, `2026-02-30T00:00:00Z`, a value one hour after the request, `knownAt=` and the parameter given twice. An `appliedAt` the service returned, sent back unchanged as `knownAt`, is always accepted. A snapshot read with `?asOf=2026-01-01` gives `400`.

**A6. Isolation.**
Given A3, T2 gets `404` for T1's snapshot id and for its employee read, and an empty list from `GET /api/v1/snapshots`.

**A7. Rejected batches.**
A rejected upload's batch has `"appliedAt": null`, and no as-known-at answer changes.

**A8. Waiting for a batch being applied** (integration tests, not the holdout suite).
While a 1M-row batch of T1 holds its lock, an as-of request with `knownAt` equal to now for T1 does not answer until the batch commits, and then includes it. The same request for T2 answers at once.

## 5. Out of scope

- Listing or exporting all employees of a snapshot (PRP-004 streams that).
- Updating, renaming or deleting snapshots, and comparing two snapshots.
- `knownAt` with a time-zone offset, or local time.
- `knownAt` on the batch endpoints.
- Computed fields (PRP-003).
- Authentication (a later PRP).
- Anything that changes PRP-001 ingestion, except recording `applied_at` on the batch.

## 6. Context for the agent

### 6.1 Read first

- `CLAUDE.md`, `specs/README.md`, and **PRP-001 in full**: this PRP extends its API, data model and lock.
- PostgreSQL advisory locks: https://www.postgresql.org/docs/current/functions-admin.html#FUNCTIONS-ADVISORY-LOCKS
- PostgreSQL row security and `FORCE`: https://www.postgresql.org/docs/current/ddl-rowsecurity.html
- Npgsql date and time types: https://www.npgsql.org/doc/types/datetime.html

### 6.2 Current state of the repository

PRP-001 2.1 is implemented on `main`: the tree in PRP-001 6.3, migrations `0001` to `0005`, 142 unit and 204 integration tests. The relevant parts:

```
db/migrations/0001_tenants.sql … 0005_drop_compensation_value_foreign_keys.sql
src/CompLedger.Core/Ingestion/RowParser.cs              # date rules (CheckDate), reused for asOf
src/CompLedger.Infrastructure/Db/TenantScope.cs         # transaction + set_config('app.tenant_id')
src/CompLedger.Infrastructure/Ingestion/PostgresIngestor.cs   # lock, appliedAt = clock_timestamp()
src/CompLedger.Infrastructure/Queries/CompensationQueries.cs  # as-of and history over current values
src/CompLedger.Api/Endpoints/{Compensation,Ingestion,Tenant,Health}Endpoints.cs, RequestChecks.cs
tests/CompLedger.IntegrationTests/  (Testcontainers fixture, app login, API factory)
```

### 6.3 Target tree after this PRP

```
db/migrations/
├── 0006_batch_apply_time_and_value_index.sql   # applied_at on batches, backfill, index for reads by employee
└── 0007_snapshots.sql                          # snapshots table, row-level security, grants
src/CompLedger.Core/KnownAt.cs                  # parse and format instants (3.2, 3.5)
src/CompLedger.Infrastructure/Queries/CompensationQueries.cs   # extended: optional knownAt
src/CompLedger.Infrastructure/Snapshots/SnapshotStore.cs       # create, get, list
src/CompLedger.Api/Endpoints/SnapshotEndpoints.cs
tests/CompLedger.UnitTests/KnownAtTests.cs
tests/CompLedger.IntegrationTests/{KnownAt,Snapshot}Tests.cs
```

### 6.4 Technical decisions (fixed: changing one requires a PRP update first)

| Decision | Reason |
|----------|--------|
| Knowledge time is the PRP-001 apply time. No new clock, no new column on `compensation_values` | `recorded_at` and `superseded_at` already carry it, consistently ordered by the tenant lock |
| A snapshot stores `(asOf, knownAt)` only, never a copy of values | Values are never updated or deleted, so the pair reproduces the answer forever, and creation is constant time |
| Stability through the PRP-001 lock: readers with `knownAt` and snapshot creation take `pg_advisory_xact_lock_shared` on the same key, `hashtextextended(tenant_id::text, 0)` | Ingestion holds the exclusive lock from apply time to commit, so a shared lock waits exactly for in-flight applies and nothing else |
| `knownAt` is UTC with `Z` only, up to microseconds; responses always give six fractional digits | One unambiguous spelling, PostgreSQL's precision, exact round trips |
| `ingestion_batches.applied_at` stores the apply time; the batch resource returns it | Clients need exact instants to ask "right after batch X"; without it they could only guess from request times |
| A non-partial index on `compensation_values (tenant_id, employee_id, component, effective_from)` | The PRP-001 unique index covers current rows only; as-known-at reads need superseded rows too |
| Snapshot names unique per tenant through a unique index; a `23505` unique violation becomes `409` | A check-then-insert races |

### 6.5 Known gotchas

1. **Lock first, read in a later statement.** In READ COMMITTED each statement sees what was committed before **it** started. Take `pg_advisory_xact_lock_shared` in one statement, then query in the next, in the same transaction. A lock and a query in one statement can read a snapshot taken before the wait ended.
2. **Transaction-level locks only.** Use the `_xact_` variants. A session-level advisory lock outlives the request on a pooled connection.
3. **Filter by `knownAt` before the window function.** `effectiveTo` comes from `lead(effective_from)`. Computing it over current values and then filtering by `knownAt` gives wrong periods (A2). Filter inside the CTE.
4. **The boundaries are asymmetric:** `recorded_at <= K` and `superseded_at > K`. With `>=`, a value and its replacement are both known at the replacement's apply time.
5. **The existence check uses the same filter.** PRP-001 decides `404` by "any value of this employee"; with `knownAt` it is "any value known at K" (3.4 item 3).
6. **Npgsql and `timestamptz`.** Parameters must be `DateTime` with `Kind = Utc` (or `DateTimeOffset` with offset 0). Parse with `DateTime.TryParseExact(..., CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, ...)`.
7. **.NET has 100 ns ticks, PostgreSQL microseconds.** The round-trip format `"O"` writes seven fractional digits. Format with `yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'`, and reject inputs with more than six digits rather than rounding them.
8. **"Not later than the request time" uses the database clock, after the lock.** Comparing with `DateTime.UtcNow` in the API would reject an `appliedAt` the service itself returned whenever the API host's clock is behind the database's. Read `clock_timestamp()` in the transaction after `pg_advisory_xact_lock_shared` and compare there (7.2). That order is also what makes the answer stable: every later batch gets a later apply time.
9. **Migrations and `FORCE ROW LEVEL SECURITY`.** The backfill in `0006` runs as the table owner, and `FORCE` hides every row from the owner when no tenant is set. Wrap the backfill in `ALTER TABLE … NO FORCE ROW LEVEL SECURITY` and `… FORCE ROW LEVEL SECURITY`, in the same migration. Tests run migrations as a superuser, which bypasses row security, so they won't catch this unless a test runs the migrations as a non-superuser owner.
10. **Backfill of `applied_at`.** For an `Applied` batch, use the `recorded_at` of a value it inserted; else the `superseded_at` of a value it superseded; else, when every row was unchanged, `completed_at`. That batch changed no value, so any instant during it gives the same answers.
11. **A new index costs on insert.** Check the 1M-row ingestion against PRP-001 3.7 locally after adding it.

## 7. Implementation blueprint

### 7.1 Data model (normative)

```sql
-- 0006
ALTER TABLE ingestion_batches ADD COLUMN applied_at timestamptz;
-- backfill (6.5 #9, #10), then:
ALTER TABLE ingestion_batches
  ADD CONSTRAINT ck_ingestion_batches_applied_at CHECK ((status = 'Applied') = (applied_at IS NOT NULL));
CREATE INDEX ix_compensation_values_employee
  ON compensation_values (tenant_id, employee_id, component, effective_from);

-- 0007
CREATE TABLE snapshots (
  id        uuid PRIMARY KEY,                       -- Guid.CreateVersion7()
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  name      text NOT NULL,
  as_of     date NOT NULL,
  known_at  timestamptz NOT NULL
);
CREATE UNIQUE INDEX ux_snapshots_tenant_name ON snapshots (tenant_id, name);
-- row-level security: ENABLE, FORCE, policy as on the PRP-001 tables; GRANT SELECT, INSERT to comp_app
```

### 7.2 Reads and snapshot creation

```
as-of or history with knownAt K (format, real instant and lower bound already validated: 400)
  TenantScope.Begin(tenant)                                   // transaction, set_config
  SELECT pg_advisory_xact_lock_shared(hashtextextended(@tenant, 0))
  requestTime <- SELECT clock_timestamp()                     // a separate statement, after the lock
  if K > requestTime: rollback, 400
  WITH known AS (
    SELECT ... FROM compensation_values
    WHERE tenant_id = @t AND employee_id = @e
      AND recorded_at <= @K AND (superseded_at IS NULL OR superseded_at > @K)
  ), periods AS (
    SELECT ..., lead(effective_from) OVER (PARTITION BY component ORDER BY effective_from) - 1 AS effective_to
    FROM known
  ) SELECT ... (PRP-001 as-of or history filter and order)
  if no rows: EXISTS over "known" decides 404 vs 200 empty
  commit

POST /api/v1/snapshots
  validate body (400)
  TenantScope.Begin(tenant)
  SELECT pg_advisory_xact_lock_shared(...)
  knownAt <- SELECT clock_timestamp()
  INSERT INTO snapshots ...      // 23505 -> 409
  commit; log one entry; 201 + Location
```

Without `knownAt`, keep the PRP-001 query on current rows and take no lock.

### 7.3 Tasks (in order; one commit per task, message `PRP-002 Tn: <summary>`)

| #  | Task | Done when |
|----|------|-----------|
| T1 | Migration `0006`: `applied_at`, backfill, check constraint, index. The ingestor writes `applied_at`; the batch resource returns `appliedAt` | Integration tests: `appliedAt` equals the `recorded_at` of the batch's values; `null` for rejected batches; a test runs `0001`–`0006` as a non-superuser owner on data from two tenants and checks the backfill; PRP-001 tests still pass |
| T2 | `KnownAt` in Core: parse and format (3.2, 3.5) | Unit tests for every rule in 3.2: format, offsets, case, 6 vs 7 digits, invalid dates, range ends, round trip of six-digit values |
| T3 | `knownAt` on the as-of and history endpoints, shared lock, `knownAt` in responses, the U+0000 employee-path rule | Integration tests reproduce A1, A2, A5, A7 and 3.4 items 1 to 3, plus A8 for both tenants |
| T4 | Migration `0007` and the snapshot endpoints, logging | Integration tests reproduce A3, A4, A6, the `409` race (two concurrent creations with one name: one `201`, one `409`), and one log entry per snapshot |
| T5 | Measure | The PR description gives local numbers for the 3.6 budgets: 1M-row ingestion with the new index, as-known-at p95, snapshot creation p95 |

## 8. Validation loop

```bash
dotnet build CompLedger.slnx -warnaserror
dotnet format CompLedger.slnx --verify-no-changes
dotnet test tests/CompLedger.UnitTests
dotnet test tests/CompLedger.IntegrationTests
```

All PRP-001 tests must keep passing. Never weaken, skip or delete a test to make a level pass. If this PRP looks wrong or contradicts PRP-001, stop and say so in the PR description.

## 9. Definition of done

- [ ] Every rule in section 3 is implemented and covered by a test; A1 to A8 pass as integration tests.
- [ ] All validation levels are green on a clean checkout; PRP-001 tests unchanged and green.
- [ ] Migrations `0001` to `0005` are untouched.
- [ ] No secrets, hostnames or credentials in the repository.
- [ ] Nothing under `specs/`, `.claude/`, `.github/` or `metrics/` was changed.
- [ ] The PR description maps each section-3 rule to its test, pastes the validation summaries and gives the T5 measurements.

## 10. Anti-patterns

- Copying values into a snapshot table, or materialising "known at" results.
- Filtering by `knownAt` after computing periods.
- Reading without the shared lock, or with a session-level lock.
- Using `now()` for a snapshot's `knownAt`, or the application clock instead of the database clock.
- Comparing instants as strings, or rounding a seven-digit `knownAt`.
- Checking a snapshot name with a `SELECT` before the `INSERT` instead of relying on the unique index.
- Adding endpoints this PRP does not list (snapshot contents, delete, diff).

## 11. How this PRP is checked beyond the agent's view

1. **Holdout scenarios** for 3.1 to 3.5 through the HTTP API only, with other data. They use `appliedAt` to build exact `knownAt` values, so boundary cases are tested to the microsecond.
2. **Load gate** for 3.6: the PRP-001 ingestion budgets with the new index, as-known-at latency and snapshot creation on a 1M-row tenant.
3. **AI review against this PRP** on the pull request.
4. All PRP-001 holdout scenarios still run and must still pass.

## Changelog

| Version | Date       | Change |
|---------|------------|--------|
| 1.0     | 2026-11-13 | First version (Draft) |
