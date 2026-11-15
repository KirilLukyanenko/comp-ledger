# PRP-003: Computed compensation fields

| Field      | Value |
|------------|-------|
| Status     | Ready |
| Version    | 1.1 |
| Owner      | KirilLukyanenko |
| Created    | 2026-11-15 |
| Depends on | PRP-001 (2.1, implemented), PRP-002 (1.3, implemented), PRP-005 (2.1, implemented: its tools must not change) |
| Unlocks    | Computed fields over MCP (a later PRP-005 version), computed fields in the export (decided in PRP-004) |

---

## 1. Goal

On request, the as-of read of an employee also returns three **computed fields**: the target bonus amount, total fixed pay and total target cash, each with its currency and the dates it holds for. They are computed from the values the read already uses: current values, values known at `knownAt` (PRP-002), or a snapshot's values. So a snapshot's computed fields never change either. Without the request, every response stays exactly as it is now.

## 2. Why

- **Business.** HR and reviewers think in totals, not components: "what is this person's total target cash?" Today every client recomputes it from three components and has to agree on rounding, currencies and missing values. Doing it once, in the service, gives one answer everywhere, including for a past view and a frozen review.
- **Engineering.** Values are never overwritten (PRP-001) and every read can be pinned to a date and an instant (PRP-002). A computed field that is derived at read time inherits both properties for free: no new table, no migration, no recomputation when a batch is applied.
- **Scale.** The computation is a few decimal operations per employee. The cost to watch is the extra lookup it needs (3.5 item 5); a read with computed fields must stay within the same latency budget as a read without them.

## 3. What

### 3.1 Concepts

- **Input**: a PRP-001 component (`BASE_SALARY`, `ALLOWANCE`, `BONUS_TARGET_PCT`) in effect on the read's `asOf`, among the values the read uses (current, known at `knownAt`, or the snapshot's).
- **Computed field**: a value derived from inputs by a fixed formula (3.2). It is not stored anywhere.
- **Computable**: a computed field is computable on `asOf` when its required inputs are in effect and their currencies agree (3.2). Otherwise it is returned with a reason and no amount.
- **Computed period**: the dates on which the field keeps the same inputs, and so the same value (3.4).

### 3.2 Computed field catalogue (fixed in this PRP)

Listed in the order the response gives them.

| Code                | Formula | Required inputs | Optional inputs | Currency |
|---------------------|---------|-----------------|-----------------|----------|
| `TARGET_BONUS`      | `BASE_SALARY × BONUS_TARGET_PCT / 100`, rounded (3.3) | `BASE_SALARY`, `BONUS_TARGET_PCT` | none | `BASE_SALARY`'s |
| `TOTAL_FIXED_PAY`   | `BASE_SALARY + ALLOWANCE` | `BASE_SALARY` | `ALLOWANCE`: when none is in effect, it counts as 0 | `BASE_SALARY`'s; an `ALLOWANCE` in effect must have the same currency |
| `TOTAL_TARGET_CASH` | `TOTAL_FIXED_PAY + TARGET_BONUS` (the rounded bonus) | `BASE_SALARY`, `BONUS_TARGET_PCT` | `ALLOWANCE`, as above | as `TOTAL_FIXED_PAY` |

- Only `ALLOWANCE` is optional. A missing `BONUS_TARGET_PCT` is not 0: a feed that means "no bonus" sends `0`, and a missing value may be missing data. So `TARGET_BONUS` and `TOTAL_TARGET_CASH` are not computable without it.
- `BONUS_TARGET_PCT` of `0` is an ordinary input: `TARGET_BONUS` is `0` and `TOTAL_TARGET_CASH` equals `TOTAL_FIXED_PAY`.
- Tenant-defined formulas and currency conversion are out of scope (section 5).

**Reasons** a field is not computable, checked in this order; the first that applies is given:

| Reason              | When |
|---------------------|------|
| `MISSING_INPUT`     | A required input is not in effect on `asOf` |
| `CURRENCY_MISMATCH` | The field uses `ALLOWANCE`, an `ALLOWANCE` is in effect, and its currency differs from `BASE_SALARY`'s (compared exactly; both are already three uppercase letters). Whatever its amount: an `ALLOWANCE` of `0` in another currency is still a mismatch |

`TARGET_BONUS` does not use `ALLOWANCE`, so it can be computable while the two totals have `CURRENCY_MISMATCH`.

### 3.3 Arithmetic

- Exact decimal arithmetic: .NET `decimal` or PostgreSQL `numeric`. Never `double` or `float`, not even for an intermediate value.
- `TARGET_BONUS` is rounded to 4 decimal places, half away from zero: `500.00005` becomes `500.0001`. All inputs are non-negative, so this is "half up". .NET's default, `MidpointRounding.ToEven`, would give `500.0000` and is wrong here (6.5 #3).
- The sums are exact: their inputs have at most 4 decimals, so no rounding is needed or allowed. `TOTAL_TARGET_CASH` adds the rounded `TARGET_BONUS`, so the three numbers in a response always add up.
- Results can exceed the PRP-001 amount range (for example 999999999999999.9999 × 500 / 100). That is allowed: computed amounts are never stored, and amounts are JSON numbers whose scale is not part of the contract (PRP-001 3.6).

### 3.4 Computed period

For a computable field on date `asOf`, take one range per input the field uses:

- a **required input**, or an **`ALLOWANCE` in effect**: its period on `asOf`, `[effectiveFrom, effectiveTo]`, exactly as the as-of response gives it (`effectiveTo` may be open);
- an **`ALLOWANCE` not in effect** (for a field that uses it): from the open past to the day before the earliest `ALLOWANCE` with `effectiveFrom` after `asOf`, or open-ended if there is none. Because PRP-001 periods run until the next value, an `ALLOWANCE` that is not in effect on `asOf` can only be one that starts later.

The computed period is the intersection of these ranges: `effectiveFrom` is the latest start (a range open to the past sets none), `effectiveTo` the earliest end, or `null` if every range is open-ended. Both are inclusive, as in PRP-001.

All of it is computed among the values the read uses. With `knownAt`, an `ALLOWANCE` recorded after `knownAt` does not end the period (PRP-002 3.1, "as-known-at query").

A field that is not computable has `null` `effectiveFrom` and `effectiveTo`.

### 3.5 Request and semantics

1. **`include` query parameter** on the as-of read (`GET /api/v1/employees/{employeeId}/compensation`, with or without `knownAt`) and on the snapshot read (`GET /api/v1/snapshots/{snapshotId}/employees/{employeeId}/compensation`). The only accepted value is `computed`, matched exactly and case-sensitively on the query value after URL decoding, as for `knownAt` (PRP-002 3.2), and not trimmed: `include=%63omputed` is `computed`, `include=%20computed` is not. Any other value (`Computed`, `computed,x`, an empty `include=`, a value with spaces) gives `400`. Given more than once gives `400`, even with equal values, as for the PRP-001 parameters. Whether a differently cased parameter name (`Include`) is the same parameter is not part of the contract, as for the PRP-001 and PRP-002 parameters. Before this PRP, `include` was an unknown parameter on these two reads and was ignored whatever its value, so a request with an invalid `include` that got `200` now gets `400`. That change is intended; requests without `include` are not affected (item 2).
2. **Without `include`**, nothing changes: the response has no `computed` property at all (not `null`), and every PRP-001, PRP-002 and PRP-005 rule holds as written.
3. **With `include=computed`**, the response gains `computed`: an array of exactly three entries, one per field of 3.2, in catalogue order, whether computable or not.
4. **Everything else is as without it.** The same checks in the same order (tenant `401`, then query validation `400`, then lookups `404`); `include` is validated with the other query parameters. The same `404` rules: an employee with no values (as known at `knownAt`) is `404` with or without `include`. When the employee has values but none in effect on `asOf`, the response is `200` with empty `components`, and all three computed fields have `MISSING_INPUT`. The same wait for a batch being applied (PRP-002 3.4 item 4) whenever `knownAt` is given or the read is a snapshot read.
5. **One consistent read.** The inputs, their periods and the earliest later `ALLOWANCE` (3.4) come from one view of the data: a batch that commits while the read runs is either entirely in it or entirely out of it. Reads with `knownAt` and snapshot reads get this from the PRP-002 lock and known-at filter. A read of current values takes no lock, so it gets it from reading everything in one SQL statement, or in one `REPEATABLE READ` transaction (6.5 #11). Two statements in a default `READ COMMITTED` transaction are not enough.
6. **The snapshot read** names `include` as its parameter, in addition to refusing `asOf` and `knownAt` (PRP-002 3.4 item 6).
7. **Other endpoints** do not name `include`. On the history read it is an ordinary unknown parameter and is ignored, as PRP-001 3.6 says, even when it is empty or repeated: `?include=computed` answers exactly as without it, and so do `?include=anything`, `?include=` and `?include=computed&include=computed`.
8. **MCP is unchanged.** The PRP-005 tools `get_compensation` and `get_snapshot_compensation` answer exactly as their endpoints do **without** `include`. An `include` argument is an unknown argument and is ignored (PRP-005 3.2). Computed fields over MCP are a later PRP-005 version.
9. **Tenant isolation** is inherited: the inputs are the tenant's own values, read under row-level security, as in PRP-001 and PRP-002.

### 3.6 API contract

No new endpoints. Base path, JSON conventions and tenant resolution as in PRP-001 3.6.

| Method | Path | Change |
|--------|------|--------|
| GET | `/api/v1/employees/{employeeId}/compensation?asOf=…&knownAt=…&include=computed` | `include` optional (3.5). With it, the PRP-001/PRP-002 as-of response plus `computed` |
| GET | `/api/v1/snapshots/{snapshotId}/employees/{employeeId}/compensation?include=computed` | Same, on the PRP-002 snapshot read response |

Response with `include=computed` (`knownAt` and, on a snapshot read, `snapshotId` as in PRP-002):

```json
{
  "employeeId": "E-2001",
  "asOf": "2026-05-15",
  "knownAt": null,
  "components": [
    { "component": "BASE_SALARY", "amount": 60000, "currency": "EUR", "effectiveFrom": "2026-04-01", "effectiveTo": null },
    { "component": "BONUS_TARGET_PCT", "amount": 10, "currency": null, "effectiveFrom": "2026-01-01", "effectiveTo": null }
  ],
  "computed": [
    { "field": "TARGET_BONUS", "amount": 6000, "currency": "EUR",
      "effectiveFrom": "2026-04-01", "effectiveTo": null, "reason": null },
    { "field": "TOTAL_FIXED_PAY", "amount": 60000, "currency": "EUR",
      "effectiveFrom": "2026-04-01", "effectiveTo": "2026-06-30", "reason": null },
    { "field": "TOTAL_TARGET_CASH", "amount": 66000, "currency": "EUR",
      "effectiveFrom": "2026-04-01", "effectiveTo": "2026-06-30", "reason": null }
  ]
}
```

Here an `ALLOWANCE` starts on 2026-07-01: it is not in effect on 2026-05-15, so it counts as 0, and it ends the two totals' period on 2026-06-30 (3.4).

A field that is not computable: `{ "field": "TARGET_BONUS", "amount": null, "currency": null, "effectiveFrom": null, "effectiveTo": null, "reason": "MISSING_INPUT" }`.

Every entry always has all six properties. `reason` is `null` exactly when `amount` is not. The order of properties is not part of the contract (PRP-002 3.5).

### 3.7 Non-functional budgets

Starting budgets. The first CI run calibrates them; any change is recorded in the changelog with the measured numbers.

| Budget | Target |
|--------|--------|
| Existing budgets | PRP-001 3.7, PRP-002 3.6 and PRP-005 3.6 still hold. This PRP adds no index and no write |
| Computed read latency | On the PRP-002 3.6 tenant (the 1M-row file, then the 100k-row correction), 200 sequential as-of requests with `knownAt` and `include=computed` for different employees: p95 of the HTTP round trip 100 ms or less on a GitHub-hosted runner |
| Logging | Nothing new. Reads still log no entry of their own (PRP-002 3.6), and no computed amount appears in any application log line |

## 4. Acceptance examples (public)

A separate holdout suite checks the same rules through the HTTP API with different data and more edge cases. **Implement the rules in section 3, not these examples.** All reads below use `include=computed` unless they say otherwise. Rows are `employee_id,component,effective_from,amount,currency`.

**A1. Everything in effect.**
T1 uploads `E-1001,BASE_SALARY,2026-01-01,60000,EUR`, `E-1001,ALLOWANCE,2026-01-01,6000,EUR`, `E-1001,BONUS_TARGET_PCT,2026-01-01,10,`.
As of 2026-05-15: `TARGET_BONUS` 6000 EUR, `TOTAL_FIXED_PAY` 66000 EUR, `TOTAL_TARGET_CASH` 72000 EUR, each from 2026-01-01, `effectiveTo` `null`, `reason` `null`.

**A2. No allowance yet, one later.**
T1 uploads `E-2001,BASE_SALARY,2026-04-01,60000,EUR`, `E-2001,BONUS_TARGET_PCT,2026-01-01,10,`, `E-2001,ALLOWANCE,2026-07-01,3000,EUR`.
As of 2026-05-15: the response in 3.6. As of 2026-07-01: `TOTAL_FIXED_PAY` 63000 from 2026-07-01, `TOTAL_TARGET_CASH` 69000 from 2026-07-01, `TARGET_BONUS` 6000 from 2026-04-01.

**A3. A raise splits the period.**
Given A1, T1 uploads `E-1001,BASE_SALARY,2026-04-01,64000,EUR`. As of 2026-03-31: `TARGET_BONUS` 6000 from 2026-01-01 to 2026-03-31. As of 2026-04-01: `TARGET_BONUS` 6400 from 2026-04-01, `effectiveTo` `null`.

**A4. Rounding.**
T1 uploads `E-3001,BASE_SALARY,2026-01-01,1000.0001,EUR` and `E-3001,BONUS_TARGET_PCT,2026-01-01,50,`. `TARGET_BONUS` is `500.0001` (from `500.00005`, half away from zero), `TOTAL_FIXED_PAY` `1000.0001`, `TOTAL_TARGET_CASH` `1500.0002`.

**A5. Missing and mismatched inputs.**
`E-4001` has only `BASE_SALARY` 50000 EUR: `TARGET_BONUS` and `TOTAL_TARGET_CASH` are `MISSING_INPUT`, `TOTAL_FIXED_PAY` is 50000 EUR.
`E-4002` has `BASE_SALARY` 50000 EUR, `ALLOWANCE` 5000 USD and `BONUS_TARGET_PCT` 10: `TARGET_BONUS` 5000 EUR, both totals `CURRENCY_MISMATCH`.
`E-4003` has only `ALLOWANCE` 5000 EUR: all three `MISSING_INPUT`.
As of a date before any of `E-4001`'s values: `200`, empty `components`, all three `MISSING_INPUT`.

**A6. Known at and snapshots.**
Given A1, T1 creates a snapshot `{"name": "Review", "asOf": "2026-05-15"}`, then uploads `E-1001,BONUS_TARGET_PCT,2026-01-01,15,`. The snapshot read shows `TARGET_BONUS` 6000; the as-of read without `knownAt` shows 9000; the as-of read with `knownAt` equal to the first batch's `appliedAt` shows 6000.
If A2's `ALLOWANCE` row is uploaded in a second batch, then as of 2026-05-15 the totals have `effectiveTo` `null` with `knownAt` equal to the first batch's `appliedAt`, and 2026-06-30 with the second batch's.

**A7. The parameter.**
Each gives `400`: `include=Computed`, `include=`, `include=computed,other`, `include=%20computed` and `include=computed&include=computed`, on the as-of read and on the snapshot read. Without `include`, neither read has a `computed` property. The history read with `include=computed` answers exactly as without it. An employee with no values is `404` with `include=computed`.

**A8. MCP unchanged.**
`get_compensation` for `E-1001` with or without an `include` argument gives the as-of response without `computed`.

## 5. Out of scope

- Tenant-defined formulas, or any computed field not in 3.2.
- Currency conversion; exchange rates.
- Computed fields on the history read (periods of a computed field over time).
- Computed fields in MCP tools (a later PRP-005 version) and in the export (PRP-004 decides).
- Storing, caching or materialising computed values; any migration.
- Filtering or sorting employees by a computed value.
- Changing any PRP-001, PRP-002 or PRP-005 behaviour, response or log entry when `include` is not given.

## 6. Context for the agent

### 6.1 Read first

- `CLAUDE.md`, `specs/README.md`, and PRP-001, PRP-002 and PRP-005 in full: this PRP extends a read they all define, and PRP-005's tools share its code.
- .NET `decimal.Round` and `MidpointRounding`: https://learn.microsoft.com/dotnet/api/system.decimal.round
- System.Text.Json conditional ignore: https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/ignore-properties

### 6.2 Current state of the repository

PRP-001 2.1, PRP-002 1.3 and PRP-005 2.1 are implemented on `main`: migrations `0001` to `0007`, 247 unit and 676 integration tests. The relevant parts:

```
src/CompLedger.Core/Components.cs                         # the PRP-001 component catalogue
src/CompLedger.Core/KnownAt.cs
src/CompLedger.Infrastructure/Queries/CompensationQueries.cs   # as-of and history, optional knownAt, lock
src/CompLedger.Api/Operations/CompensationOperations.cs   # shared by REST and MCP: parameters, 400/404, responses
src/CompLedger.Api/Operations/SnapshotOperations.cs       # snapshot reads, shared by REST and MCP
src/CompLedger.Api/Endpoints/CompensationEndpoints.cs, SnapshotEndpoints.cs   # thin: pass raw query values
src/CompLedger.Api/Mcp/CompLedgerTools.cs                 # the seven tools, on the same operations
```

### 6.3 Target tree after this PRP

```
src/CompLedger.Core/ComputedFields.cs                     # catalogue, formulas, rounding, reasons, periods (pure)
src/CompLedger.Infrastructure/Queries/CompensationQueries.cs   # extended: earliest later ALLOWANCE when asked
src/CompLedger.Api/Operations/CompensationOperations.cs   # extended: include, computed in the as-of response
src/CompLedger.Api/Operations/SnapshotOperations.cs       # extended: include on the snapshot read
tests/CompLedger.UnitTests/ComputedFieldsTests.cs
tests/CompLedger.IntegrationTests/ComputedFieldsTests.cs
```

### 6.4 Technical decisions (fixed: changing one requires a PRP update first)

| Decision | Reason |
|----------|--------|
| Computed at read time from the values the read already selects; never stored | Known-at reads and snapshots stay exact with no extra work, and no migration is needed |
| The catalogue and the arithmetic live in Core as a pure function of the inputs in effect and the earliest later `ALLOWANCE` | Every rule in 3.2 to 3.4 can be unit-tested without a database |
| `decimal` arithmetic, `MidpointRounding.AwayFromZero` to 4 places, for `TARGET_BONUS` only | One exact, documented answer; matches PostgreSQL `round(numeric, 4)` |
| Opt-in through `include=computed`; without it the response has no `computed` property | Existing clients, tests and holdout scenarios see byte-for-byte the same responses |
| The MCP tools pass "`include` not given" to the shared operations | PRP-005 says a tool is its endpoint; computed fields over MCP need their own PRP-005 version |
| The earliest later `ALLOWANCE` is read with the inputs, in the same SQL statement or the same `REPEATABLE READ` transaction, with the same known-at filter, and only when `include=computed` | One consistent view of the data (3.5 item 5); no extra work for reads without it |

### 6.5 Known gotchas

1. **The as-of query only returns values in effect.** A later `ALLOWANCE` is not among them, but it ends the totals' period (3.4, A2). Look it up separately: the earliest `effective_from > asOf` for `ALLOWANCE`, among the values the read uses.
2. **Apply the known-at filter to that lookup too.** With `knownAt`, an `ALLOWANCE` recorded after `knownAt` must not end the period (A6). Reuse the PRP-002 filter (`recorded_at <= K AND (superseded_at IS NULL OR superseded_at > K)`), inside the same transaction and after the same lock.
3. **`Math.Round` and `decimal.Round` round half to even by default.** Pass `MidpointRounding.AwayFromZero` (A4).
4. **No `double`, not even through JSON.** Keep amounts `decimal` end to end. Do not compute `pct / 100` as a `double` or a `float` literal.
5. **`computed` absent, `knownAt` present.** PRP-002 requires `"knownAt": null` when no `knownAt` was given, so a global "ignore nulls" setting is wrong. Ignore only the `computed` property when it is null (`[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`), or use a separate response type.
6. **The shared operations serve MCP too.** Adding a parameter to `CompensationOperations` or `SnapshotOperations` must not change what a tool returns. The tool passes "not given"; every PRP-005 test must pass unchanged.
7. **`include` is validated with the other query parameters, before the lookup.** An invalid `include` for an unknown employee is `400`, not `404`; for a malformed `snapshotId` it is also `400` (PRP-002: query validation before lookups).
8. **The snapshot read refuses `asOf` and `knownAt` but now accepts `include`.** Do not make it refuse every parameter.
9. **Empty `components` is not `404`.** An employee with values but none in effect gets three `MISSING_INPUT` entries (A5).
10. **Currency comparison is ordinal.** Both currencies already passed `^[A-Z]{3}$` (PRP-001 3.3); compare with `string.Equals(a, b, StringComparison.Ordinal)`.
11. **`READ COMMITTED` gives each statement its own view.** PostgreSQL's default isolation lets two statements of one transaction see different committed data (the same fact as PRP-002 6.5 #1). A current read takes no lock, so the as-of query and a separate later-`ALLOWANCE` query can straddle a batch's commit: the old bonus percent with the new `ALLOWANCE`'s date, or the reverse. Either compute the later `ALLOWANCE` in the same statement as the inputs (a subquery or a window over the same rows), or run the read's statements in a `REPEATABLE READ` transaction. Reads with `knownAt` are consistent either way, because the known-at filter excludes every batch applied after `knownAt`. The holdout scenarios cannot provoke this race; cover it with a test or say in the PR description how the read guarantees it.

## 7. Implementation blueprint

### 7.1 Data model

None. No migration, no new column, no new index. The PRP-002 index on `compensation_values (tenant_id, employee_id, component, effective_from)` serves the extra lookup.

### 7.2 Algorithm

```
as-of read (REST) with include=computed:
  validate asOf, knownAt, include (400)
  in one view of the data (3.5 item 5, 6.5 #11; with the PRP-002 lock when knownAt is given or for a snapshot read):
    inputs  <- the existing as-of query: the values in effect on asOf, with their periods
    nextAllowance <- min(effective_from) over <the same value set>
                     WHERE employee_id = @e AND component = 'ALLOWANCE' AND effective_from > @asOf
                     (in the same statement as inputs, or in the same REPEATABLE READ transaction)
    existence check as today (404)
  computed <- ComputedFields.For(asOf, inputs, nextAllowance)

ComputedFields.For(asOf, inputs, nextAllowance):
  base, pct, allowance <- inputs by component (allowance may be absent)
  for each field in catalogue order:
    if a required input is absent: MISSING_INPUT
    else if field uses ALLOWANCE and allowance present and allowance.currency != base.currency: CURRENCY_MISMATCH
    else:
      bonus = round(base.amount * pct.amount / 100, 4, AwayFromZero)
      fixed = base.amount + (allowance?.amount ?? 0)
      amount = TARGET_BONUS: bonus | TOTAL_FIXED_PAY: fixed | TOTAL_TARGET_CASH: fixed + bonus
      ranges = periods of the required inputs
             + (field uses ALLOWANCE: allowance period if present, else (open, nextAllowance - 1 day or open))
      effectiveFrom = max of the starts that are set; effectiveTo = min of the ends that are set, or null
```

### 7.3 Tasks (in order; one commit per task, message `PRP-003 Tn: <summary>`)

| #  | Task | Done when |
|----|------|-----------|
| T1 | `ComputedFields` in Core: catalogue, formulas, rounding, reasons, periods | Unit tests for every rule in 3.2 to 3.4: each formula, `0` percent, the rounding midpoint (A4) and a value just below it, each reason and their order, a mismatch that leaves `TARGET_BONUS` computable, periods with an `ALLOWANCE` in effect, absent with a later one, absent with none, and a required input that ends first |
| T2 | `include` on the as-of and snapshot reads, the later-`ALLOWANCE` lookup, `computed` in the response | Integration tests reproduce A1 to A8; every `400` case of 3.5 item 1 on both reads; `include` before the `404` (6.5 #7); without `include` neither response has `computed`; all existing tests pass unchanged |
| T3 | Measure | The PR description gives local numbers for 3.7: p95 of the as-of read with `knownAt` and `include=computed` on the 1M-row tenant, and the same read without `include` for comparison |

## 8. Validation loop

```bash
dotnet build CompLedger.slnx -warnaserror
dotnet format CompLedger.slnx --verify-no-changes
dotnet test tests/CompLedger.UnitTests
dotnet test tests/CompLedger.IntegrationTests
```

All PRP-001, PRP-002 and PRP-005 tests must keep passing unchanged. Never weaken, skip or delete a test to make a level pass. If this PRP looks wrong or contradicts an earlier PRP, stop and say so in the PR description.

## 9. Definition of done

- [ ] Every rule in section 3 is implemented and covered by a test; A1 to A8 pass as tests.
- [ ] All validation levels are green on a clean checkout.
- [ ] No existing test file changes, except files this PRP adds to. No existing test method changes.
- [ ] No migration is added and migrations `0001` to `0007` are untouched.
- [ ] No secrets, hostnames or credentials in the repository.
- [ ] Nothing under `specs/`, `.claude/`, `.github/` or `metrics/` was changed.
- [ ] The PR description maps each section-3 rule to its test, pastes the validation summaries and gives the T3 measurements.

## 10. Anti-patterns

- Storing computed values, or recomputing them when a batch is applied.
- `double`, `float` or banker's rounding anywhere in the computation.
- Treating a missing `BONUS_TARGET_PCT` as `0`, or a missing `BASE_SALARY` as `0`.
- Converting currencies, or picking one of two currencies.
- Ending a period only at the inputs in effect and ignoring a later `ALLOWANCE`.
- Looking up the later `ALLOWANCE` among current values on a known-at or snapshot read.
- Adding `computed` (even as `null` or `[]`) to responses without `include=computed`, or to the history read.
- Changing what an MCP tool returns or which arguments it lists.
- Adding endpoints, parameters or fields this PRP does not list.

## 11. How this PRP is checked beyond the agent's view

1. **Holdout scenarios** for 3.2 to 3.6 through the HTTP API only, with other data: formulas and rounding with values chosen to hit midpoints, every reason, periods around later and earlier values, known-at and snapshot reads using `appliedAt`, and the parameter rules.
2. **Load gate** for 3.7: computed read latency on the 1M-row tenant; the existing budgets.
3. **AI review against this PRP** on the pull request.
4. All PRP-001, PRP-002 and PRP-005 holdout scenarios still run and must still pass, which also checks that responses without `include` and the MCP tools did not change.
5. **Not checked by the holdout scenarios:** the consistency of a read of current values while a batch commits (3.5 item 5). A black-box test cannot place a commit between two statements of one read. The scenarios check consistency for reads with `knownAt` and snapshot reads; for current reads it rests on the implementer's tests and review (6.5 #11).

## Changelog

| Version | Date       | Change |
|---------|------------|--------|
| 1.0     | 2026-11-15 | First version (Draft). Owner's decisions: a fixed catalogue of three fields (no tenant formulas); a missing `ALLOWANCE` counts as 0; computed fields only on the as-of and snapshot reads, opt-in with `include=computed`; no MCP change in this PRP |
| 1.1     | 2026-11-15 | Closes the 6 gaps found while writing the holdout scenarios. A current read has to see one view of the data: two statements in a `READ COMMITTED` transaction can straddle a batch's commit, so the inputs and the later `ALLOWANCE` come from one statement or one `REPEATABLE READ` transaction (3.5 item 5, 6.4, 6.5 #11, 7.2, 11.5). `include` is matched after URL decoding, as `knownAt` is; the case of its name is not part of the contract; it newly gives `400` for invalid values on the two reads that name it, and stays ignored on the history read even when empty or repeated (3.5 items 1 and 7). An `ALLOWANCE` of `0` in another currency is still `CURRENCY_MISMATCH` (3.2). Status Ready: key decisions made by the owner; holdout scenarios written (42 methods, 88 cases) and the 3.7 load test added. On the image of `main` without PRP-003 code, only the expected cases fail (no `computed`, or no `400` for an invalid `include`), and the PRP-001, PRP-002 and PRP-005 suites pass |
