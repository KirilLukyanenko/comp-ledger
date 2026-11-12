# CLAUDE.md

Rules for AI agents working in this repository. Read this file fully before you start.

## What this is

`comp-ledger` is a multi-tenant compensation data service: .NET 10, ASP.NET Core Minimal APIs, PostgreSQL 17.
AI agents write the code. Humans write the specs and own the verification.

## The spec is the source of truth

- Every change starts from a PRP (Product Requirement Prompt) in `specs/`. Read `specs/README.md` once, then the PRP you were given, top to bottom, before you write any code.
- Implement what the PRP says: no more, no less. Do not add endpoints, columns, packages or options that the PRP doesn't mention.
- If the PRP is ambiguous, contradicts itself or contradicts the code, **stop and report it** in your final message or PR description. Don't guess and don't "fix" the spec.
- A PRP's "Technical decisions" section is binding. Changing one requires a spec update first, which a human makes.

## Do not edit

- `specs/**`: specs are written and versioned by humans.
- `.claude/**`, `.github/**`, `metrics/**`, `CLAUDE.md`: the pipeline and its records.

If you think one of these needs to change, say so in the PR description.

## Working loop

1. Read the PRP. List its tasks (section "Tasks") and do them in order.
2. Make one commit per task (see "Commits" below).
3. After each task, run the validation levels that the task's "Done when" column names.
4. Before you open a PR, run all levels on a clean build and paste their summaries into the PR description.
5. In the PR description, map every behavioural rule in the PRP to the test that covers it.

## Commands

```bash
dotnet build CompLedger.slnx -warnaserror           # Level 1
dotnet format CompLedger.slnx --verify-no-changes   # Level 1
dotnet test tests/CompLedger.UnitTests              # Level 2
dotnet test tests/CompLedger.IntegrationTests       # Level 3, needs Docker
```

## Code rules

- C# with nullable enabled and warnings as errors. Package versions live in `Directory.Packages.props` only.
- Async all the way down. No `.Result`, no `.Wait()`, no synchronous I/O on request bodies.
- Data access goes through the style the PRP fixes (currently Npgsql, Dapper and binary COPY). Don't introduce a second one.
- Keep pure logic in `CompLedger.Core` (no I/O there) so it can be unit tested without a database.
- Prefer small, explicit code over abstractions the PRP doesn't need.

## Multi-tenancy (always applies)

- Every tenant-owned table has `tenant_id` and a row-level security policy.
- Tenant-scoped database work runs inside a transaction that first calls `set_config('app.tenant_id', @tenantId, true)`. Never use session-level `SET` for the tenant.
- The application never connects as a superuser or as the table owner. Tests use the same non-superuser app login.
- An id from another tenant gives `404`, never `403`: don't reveal that it exists.

## Security and privacy

- No secrets, passwords, tokens, hostnames or credentialed connection strings in the repository. Configuration comes from environment variables. Tests generate random passwords at runtime.
- Compensation data is personal data. Never log row contents, amounts, or employee ids together with amounts.

## Tests

- Never weaken, skip, delete or `[Skip]` a test to get a green build. If you believe a test is wrong, stop and say why.
- Integration tests use Testcontainers PostgreSQL. Each test creates its own tenant instead of resetting the database.
- Holdout behavioural scenarios and load gates exist outside this repository and run in CI. They test the PRP's rules with different data. Don't look for them; implement the rules, not the examples.

## Commits

Message format: `PRP-NNN Tn: <imperative summary>`. End every commit you make while implementing a PRP with these trailers:

```
Spec: PRP-NNN
Agent-Run: <run id passed to /implement-prp>
```

These trailers are how the project measures which changes were written by agents and which needed human edits. Never invent an `Agent-Run` value. If no run id was given, omit that trailer.
