# comp-ledger

A multi-tenant compensation data service (.NET 10, PostgreSQL 17) whose code is written by Claude Code agents from specs, and checked by tests the agents never see.

The service is the vehicle. The point is the process around it: how to hand a feature to an AI agent and trust what comes back. Humans write the specs and own the checks; agents write the code. Every agent run is logged with its cost and outcome, failed runs included, and the numbers below come only from that log.

**At a glance**

- 5 specs implemented end to end by Claude Code agents. No human commit touches `src/`, `tests/` or `db/`.
- Checked by 1,347 holdout scenarios the agents never see, plus a load gate: 1M rows ingested in about 7 s, a 500k-row CSV export in about 3 s, memory flat.
- 9 logged agent runs, $71.29 at API prices, 8 of 9 spec versions passed on the first attempt. Failures are listed with what changed because of them ([below](#what-went-wrong-and-what-changed-because-of-it)).
- .NET 10, PostgreSQL 17 with row-level security, an MCP server tried live from Claude Code.

## Status

| Spec | What it covers | Status |
|------|----------------|--------|
| [PRP-001](specs/001-tenant-ingestion-effective-dating.prp.md) | CSV ingestion of compensation values with effective dating, append-only corrections, atomic batches, tenant isolation enforced by PostgreSQL row-level security | Implemented, version 2.1 |
| [PRP-002](specs/002-as-known-at-and-snapshots.prp.md) | "As known at" queries over the two time axes PRP-001 already stores, and named snapshots that give the same answer forever without copying data | Implemented, version 1.3 |
| [PRP-003](specs/003-computed-fields.prp.md) | Computed fields on request (`include=computed`): target bonus, total fixed pay and total target cash, from the same values as the read, so they work with "as known at" and snapshots | Implemented, version 1.2 |
| [PRP-004](specs/004-streaming-export.prp.md) | Streaming CSV export of every value in effect on a date, now, as known at an instant, or through a snapshot; the file names its instant, so asking again gives the same bytes, and a slow download never holds up uploads | Implemented, version 1.3 |
| [PRP-005](specs/005-mcp-server.prp.md) | An MCP server inside the API at `/mcp`: seven tools that are the read and snapshot endpoints, with the same inputs, answers, errors and tenant check | Implemented, version 2.1 |

Next: computed fields over MCP and in the export (later versions of PRP-005 and PRP-004).

## How a feature gets built

```
 human ──writes──▶ PRP (spec)  ──read only──▶  separate agent session ──▶ holdout scenarios (private repo)
                       │                                                          │
                       ▼                                                          │
 tools/run-prp.cs: new git worktree ──▶ Claude Code, /implement-prp               │
                       │   tasks in order, one commit per task, tests              │
                       │   tools/verify.sh after each task                         │
                       │   reviewer sub-agent checks every rule of the spec        │
                       ▼                                                          ▼
 wrapper re-runs the gates itself ──▶ metrics/runs.jsonl     CI: gates, holdout scenarios, load gate
                                                                       │
                                                         human reads results, merges
```

- **The spec is the source of truth.** A [PRP](specs/README.md) (Product Requirement Prompt) states the observable behaviour, every error case, the technical decisions the agent may not change, known traps, and ordered tasks with a checkable "done when". If code and spec disagree, the spec gets a new version; nobody silently picks one.
- **Holdout scenarios.** A separate agent session reads only the spec and writes black-box HTTP scenarios with different data from the spec's examples. They live in a private repository. The implementing agent can't read them: they are in another repository, `.claude/settings.json` denies `gh`, `curl`, `git clone` and edits to specs, CI and metrics, and `run-prp.cs` denies reads of the scenarios folder.
- **The wrapper doesn't trust the agent.** [`tools/run-prp.cs`](tools/run-prp.cs) runs the agent headless in its own worktree, then runs [`tools/verify.sh`](tools/verify.sh) itself (build with warnings as errors, format, unit, integration on Testcontainers) and appends one line to [`metrics/runs.jsonl`](metrics/runs.jsonl). It refuses to start on an uncommitted spec, because the agent only sees committed files.
- **CI** ([`ci.yml`](.github/workflows/ci.yml)) runs the same gates, then builds the API image, checks out the holdout scenarios with a read-only deploy key and runs them and the load gate. The image is built before the scenarios are on disk, and the public log shows counts only. On pull requests, an AI review checks the diff against the spec. A secret scan runs over the full history on every push.
- **Agent setup** is in [`CLAUDE.md`](CLAUDE.md) and [`.claude/`](.claude): skills `/implement-prp` and `/verify`, a read-only `reviewer` sub-agent, an `implementer` sub-agent for parallel work in worktrees, and the permission rules.

## Results so far

All from [`metrics/runs.jsonl`](metrics/runs.jsonl), the CI logs and the holdout repository, for five specs and nine agent runs. That is a worked example, not a benchmark. Model: Claude Code's default, which was `claude-fable-5-2` for runs 1 to 5 and `claude-opus-5-5` for runs 6 to 9; the wrapper does not choose one, so runs 6 to 9 are not a like-for-like comparison. Cost is the API-list-price estimate Claude Code reports; the runs were on a subscription, so it is not what was billed.

| Run | Spec | Agent | Commits | Gates | Holdout scenarios | 1M-row upload |
|-----|------|-------|---------|-------|-------------------|---------------|
| 1 | PRP-001 1.3 | 34 turns, 5.6 min, $2.75 | 0 | build failed | not run | not run |
| 2 | PRP-001 1.4 | 138 turns, 47.4 min, $16.68 | 7 | all pass | 496 / 496 | 46.9 s local; 34.4, 56.8, 58.0, 58.2 s on CI |
| 3 | PRP-001 2.0 | 42 turns, 12.5 min, $4.84 | 2 | all pass | 509 / 509 | 7.8 s local; 9.2, 6.8 s on CI |
| 4 | PRP-002 1.2 | 27.9 min, $13.42 (turns: see below) | 5 | all pass | 360 / 360, and PRP-001's 509 / 509 | 8.9 s local; 7.9 s on CI, with PRP-002's new index |
| 5 | PRP-005 1.2 | 149 turns, 31.9 min, $15.22 | 5 | all pass | 270 / 270, and 360 / 360, 509 / 509 for the earlier specs | 9.7 s local; 5.9 s on CI, with `/mcp` mapped |
| 6 | PRP-005 2.0 | 49 turns, 12.1 min, $2.46 | 3 | all pass | 293 / 293, and 360 / 360, 509 / 509 for the earlier specs | 12.8 s local; 8.0 s on CI |
| 7 | PRP-003 1.1 | 74.5 min, $6.01 (turns: see below) | 3 | all pass | 88 / 88, and 293 / 293, 360 / 360, 509 / 509 for the earlier specs | 8.9 s local; 6.0, 8.0 s on CI |
| 8 | PRP-004 1.1 | 28.6 min, $7.37 (turns: see below) | 6 | all pass | 97 / 97, and 293 / 293, 88 / 88, 509 / 509, 360 / 360 for the earlier specs | 10.5 s local; 7.1 s on CI, after run 9 |
| 9 | PRP-004 1.2 | 14.9 min, $2.54 (turns: see below) | 1 | all pass | not re-run: tests only; all pass on CI after the merge | not re-run |

Run 4's PRP-002 budgets (p95 of 200 "as known at" reads after a 1M-row upload and a 100k-row correction, and of 20 snapshot creations, budget 100 ms each): 3.7 ms and 5.9 ms locally, 2.2 ms and 1.6 ms on CI.

Run 5's PRP-005 budget (p95 of 200 `get_compensation` tool calls with `knownAt` on the same data, budget 100 ms): 5.1 ms locally, 2.1 ms on CI. After run 6: 6.5 ms locally, 3.2 ms on CI; after run 7: 4.2 ms locally, 2.1 and 3.1 ms on CI.

Run 7's PRP-003 budget (p95 of 200 as-of reads with `knownAt` and `include=computed` on the same data, budget 100 ms): 3.1 ms locally, 2.3 ms on CI. The same reads without `include` took 3.3 ms locally and 2.2 ms in that CI run. The first CI run on `main` passed this gate without printing it, because the load summary filter in `ci.yml` did not list the line; that is fixed.

Run 8's PRP-004 budgets (on the same data: one export of the 500,001 values in effect on a date, request to last byte, budget 30 s; memory growth of that export in a fresh API process, budget 64 MiB; one upload while an export is open and paused, budget 10 s): 4.1 s, 15.3 MiB and 0.05 s locally; 3.2 s, 10.1 MiB and 0.03 s on CI after run 9.

Minutes are the wall clock of the agent process. For run 4, Claude Code reported 6 turns and 1.5 minutes, far less than the 27.9 minutes the process ran: its counters covered only the end of the session, most likely the part after the agent waited for background work. Its cost covers the whole session. The turn count is therefore unknown, as it is for runs 7 to 9 (2, 2 and 10 turns and 0.8, 0.3 and 8.8 minutes reported, against 74.5, 28.6 and 14.9 minutes run), and [`metrics/README.md`](metrics/README.md) records the clarification.

With the definitions fixed in [`metrics/README.md`](metrics/README.md) before the first run:

- **First-attempt success:** 8 of 9 spec versions (PRP-001 1.4 and 2.0, PRP-002 1.2, PRP-005 1.2 and 2.0, PRP-003 1.1, PRP-004 1.1 and 1.2). PRP-004 1.1 meets the definition, but after its merge one of its own integration tests failed on CI on `main`; see below. One deviation from the definition: it asks for the holdout scenarios and load gate to pass in CI on the agent's branch. CI isn't run on agent branches, so both ran locally on the branch's image, and CI passed afterwards on `main` with the same code.
- **Merged without human edits:** 8 of 8 merges. No human commit touches `src/`, `tests/` or `db/`. The agents wrote 70 files, 12,302 lines including tests (313 unit, 791 integration).
- **Cost:** PRP-001 $24.27 and 65.5 minutes of agent time over three runs, the failed one included; PRP-002 $13.42 and 27.9 minutes in one run; PRP-005 $17.68 and 44.0 minutes over two runs (1.2, then 2.0); PRP-003 $6.01 and 74.5 minutes in one run; PRP-004 $9.91 and 43.5 minutes over two runs (1.1, then 1.2 for a test fix). All nine runs: $71.29.
- **Memory:** peak RSS between 112.1 and 123.3 MiB in every load run, for 100k and for 1M rows (budget 300 MiB). Going from 100k to 1M rows never added more than 0.9 MiB (budget 64 MiB), so the upload streams.

### What went wrong, and what changed because of it

- **Run 1 stopped at the first task.** Claude Code ignores a project's allowed-command list in a folder that was never marked as trusted, so every `dotnet build` and `git add` was denied. The agent stopped and reported the blocker instead of writing code it couldn't build. Its PR description also listed 7 ambiguities in the spec that the holdout author had missed, one of them a real race: `now()` in PostgreSQL is the transaction start, which is before the lock is taken. All 7 went into version 1.4, and `run-prp.cs` now checks trust before it starts.
- **The first holdout run against run 2 hit two harness bugs.** First nothing ran: the newest xUnit v3 needs an opt-in to the new test runner on .NET 10, which the agent had warned about in its PR. Then 2 of 496 scenarios failed because Docker prefixed every log line with a timestamp, so the API's JSON logs didn't parse. The harness was fixed; the scenarios were not touched.
- **The throughput budget was nearly missed on CI.** Run 2 measured, without being asked, that per-row foreign key checks took 19.6 s and 19.8 s of a 1M-row merge against 4.3 s for the insert itself. CI then measured the whole upload at 34.4 to 58.2 s against a 60 s budget. Spec 2.0 dropped those foreign keys (the pipeline already guarantees both references), the holdout scenarios were updated first and failed 12 of 509 on the old code as expected, and run 3 brought the upload to 6.8 to 9.2 s on CI.
- **Writing the holdout scenarios surfaced 60 ambiguities in PRP-001 before any code existed** (45, 10, 3 and 2 across versions 1.0 to 1.3). 58 were decided in the spec. The remaining edge cases are listed in the holdout repository with the assumption each scenario makes; 5 are open now.
- **For PRP-002 they surfaced 12 more (9 and 3), all decided before the agent started.** One was a rule that could not be met: "U+0000 in an employee id in the URL gives 404". Kestrel refuses `%00` in a path with `400` before the application sees the request, and the agent's in-process integration tests skip that parser, so they would have shown `404`. The scenarios run against the real container and caught it, so the spec now leaves that `400` to the server.
- **PRP-002 contradicted itself about PRP-001's tests.** It said they must stay unchanged, but it adds fields to responses whose exact shape those tests pin. The agent changed four lines (three expected property lists and one raw-SQL seed), said so first in its PR description, and the owner checked that no test was weakened before merging. Spec 1.3 fixes the wording, along with the other points the agent listed.
- **CI ran PRP-002's load tests on `main` before PRP-002 existed** and failed. CI now runs a spec's holdout scenarios and load tests only once the history has an implementer commit for it.
- **The first holdout run against run 5 failed 100 of 270 PRP-005 scenarios, all on one harness line.** It looked for the `Allow` header among the response headers; .NET keeps it with the content headers and throws before any check runs. The harness was fixed; no assertion changed, and the rerun passed 270 of 270.
- **The agent left one decision to the owner.** When a tool failed with `500`, the only trace was `Status: 500` in the call's log entry, because the spec forbids argument values in logs and the exception is caught inside the tool. Spec 2.0 decided it: that entry is now at `Error` and carries the exception's type name, never its message, which can quote argument values. Run 6 implemented it in 12 minutes. The holdout scenarios cannot cause a tool `500` from outside, so only the agent's own test checks that entry; the spec says so.
- **Writing PRP-003's scenarios found a race in its own spec.** Version 1.0 let a current read without `knownAt` take the input values and the next `ALLOWANCE` date in two statements; under `READ COMMITTED` a batch can commit between them, and the totals would then be cut by an allowance the answer does not show. Spec 1.1 asks for one statement or one `REPEATABLE READ` transaction, and the agent used one statement. Five smaller gaps were closed in the same version, all before the agent started. The holdout cannot force that interleaving from outside, and the spec says so.
- **Run 7 could not run `tools/verify.sh`.** The allow rule matches the exact command `bash tools/verify.sh`, and the agent's call did not match it, so it ran the four gates one by one; the wrapper ran `verify.sh` itself afterwards. Its measure-only task T3 became an empty commit that carries the numbers.
- **Updating the scenarios for 2.0 showed that a 1.3 rule could not be met.** 1.3 said a string argument .NET cannot decode (an unpaired surrogate such as `\ud800`) is a `400` tool error. The rule came from the agent's own note, and no test checked it. The MCP SDK refuses that request while reading its parameters, before any tool runs, as reproduced in a local test. 2.0 makes it a protocol error, the 1.3 code already passed the new scenarios, and run 6 added the missing test.
- **One of the agents' tests is flaky.** PRP-002's A8 test (a read waits for a 1M-row batch being applied) failed once in run 6 and once on CI on `main` (run 43706843465), on a commit that changed only documentation; the rerun passed. The service answered correctly: the test takes its instant as soon as it sees the batch's lock, but the batch fixes its apply time one statement later, so under load the instant can come first and `404` is the right answer. PRP-004 task T0 had the agent fix the test in run 8, so the code stays free of human edits.
- **Run 8 merged green and turned `main` red.** Its holdout scenarios and load gate passed on its image, but one of its own integration tests failed on CI after the merge (run 43722022445). The test searched every export log entry of its class for forbidden values, and another test of the class legitimately logs an `AsOf` of 2025-07-01, which was on the list; test order is not fixed, so it had passed locally. The service was right. PRP-004 1.2 gave the agent one test-only task; run 9 fixed it, found the same leak by chance in two more log tests, and `main` was green again after its merge. The residue it reported is still open: a forbidden amount such as `6000` can match digits of a test's own ids or instants (about 0.1 to 0.2% per run, by its estimate).
- **A PRP-002 holdout scenario contradicted PRP-004.** It listed `/api/v1/snapshots/{id}/export` among the paths that must be `404`, and PRP-004 adds that path. Run 8's code failed it as intended; the scenario was fixed, not the code.

Not yet measured: the CI AI review has not run, because the repository has no API key set yet, and no defects after merge have been recorded (the flaky A8 test and run 8's logging test are faults in tests, not broken rules of a spec).

## Repository

```
specs/            PRPs, the template and the rules for writing them
.claude/          skills, sub-agents and permissions for Claude Code
CLAUDE.md         rules every agent reads first
tools/            verify.sh (gates), run-prp.cs (agent runs), dispatch.cs (several at once), metrics-report.cs
metrics/          runs.jsonl, written by run-prp.cs; dispatches.jsonl, by dispatch.cs; definitions in README.md
.github/          CI and the AI review prompt
src/, tests/, db/ the service, written by the agents
```

## Running it

You need the .NET 10 SDK, Docker, and Git Bash on Windows.

```bash
bash tools/verify.sh                  # build, format, unit and integration tests
dotnet run tools/metrics-report.cs    # the run table above, from metrics/runs.jsonl
```

To run an agent on a spec, you also need the Claude Code CLI, signed in, and this folder marked as trusted in Claude Code. The run uses your Claude Code usage and costs real money at API prices.

```bash
dotnet run tools/run-prp.cs -- 001 --max-budget-usd 30
```

### Several specs at once

[`tools/dispatch.cs`](tools/dispatch.cs) runs several PRPs in parallel, each through `run-prp.cs` in its own worktree, under one budget:

```bash
dotnet run tools/dispatch.cs -- --ready --budget-usd 40 --dry-run   # the plan only
dotnet run tools/dispatch.cs -- --ready --budget-usd 40             # every Ready spec, two at a time
dotnet run tools/dispatch.cs -- 003 004 --parallel 2 --budget-usd 30
```

- **Plan.** A spec is skipped, with the reason, if it is not Ready, if a spec it depends on is not Implemented (a dependency in the same batch isn't merged yet, so it doesn't count), if a run of it is already in progress, or if a run of its current version passed the gates and waits for review.
- **Budget.** A run starts only if the money spent so far, plus the cap of every running run, plus its own cap fits in `--budget-usd`. So the total holds even if every run spends its whole cap (`--max-budget-usd`, default budget / parallel).
- **Conflicts.** When the runs are done, every pair of branches is merged in memory with `git merge-tree`: independent, both change the same files but merge cleanly, or conflict. The merge order stays a human decision. `dotnet run tools/dispatch.cs -- conflicts <branch> <branch>` runs only this check.

The live state is in `.prp-run/dispatches/<id>.json` (gitignored); a finished dispatch appends one line to `metrics/dispatches.jsonl`. With `--replay` and `--runs-file` it runs the whole pipeline, worktrees and gates included, without calling the agent. The first such check: two specs in parallel took 3.3 minutes instead of 6.6, and the third waited, then was skipped for the budget.

The holdout scenarios are private, so a fork's CI skips them.

### Using it from an MCP client

A running API serves MCP at `/mcp` (stateless Streamable HTTP; both protocol revisions 2026-07-28 and 2025-06-18). The tenant goes in the same `X-Tenant-Id` header as for REST. For Claude Code, a project `.mcp.json`:

```json
{
  "mcpServers": {
    "comp-ledger": {
      "type": "http",
      "url": "http://localhost:8080/mcp",
      "headers": { "X-Tenant-Id": "${COMP_LEDGER_TENANT_ID}" }
    }
  }
}
```

The tools are `get_compensation`, `get_compensation_history`, `get_ingestion`, `list_snapshots`, `get_snapshot`, `get_snapshot_compensation` and `create_snapshot`; [PRP-005](specs/005-mcp-server.prp.md) section 3.3 lists their arguments. Uploads stay REST-only.

Tried live on 16 November, with the `.mcp.json` above unchanged, from Claude Code 2.1.325 in headless mode (`claude -p --mcp-config .mcp.json`). The API and PostgreSQL ran in Docker, with three employees, a correction and a snapshot. Claude Code found the tools without being told their names. It answered an as-of question, compared a snapshot with the current value, and explained the difference from the history as a correction of the same period. It reported the `404` for an unknown employee. It also created a snapshot and read a value `knownAt` the first snapshot's instant. That took 11 tool calls in two questions, $0.37 at API prices. The server logged one entry per call, with no employee id, amount or snapshot name. `/mcp` answered `401` without a tenant and with an unknown one.

No secrets live in this repository. Database credentials are generated per test run, and keys come only from environment variables or CI secrets. A gitleaks hook (`git config core.hooksPath .githooks`) and the CI scan check every commit.
