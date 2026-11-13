# comp-ledger

A multi-tenant compensation data service (.NET 10, PostgreSQL 17) whose code is written by Claude Code agents from specs, and checked by tests the agents never see.

The service is the vehicle. The point is the process around it: how to hand a feature to an AI agent and trust what comes back. Humans write the specs and own the checks; agents write the code. Every agent run is logged with its cost and outcome, failed runs included, and the numbers below come only from that log.

## Status

| Spec | What it covers | Status |
|------|----------------|--------|
| [PRP-001](specs/001-tenant-ingestion-effective-dating.prp.md) | CSV ingestion of compensation values with effective dating, append-only corrections, atomic batches, tenant isolation enforced by PostgreSQL row-level security | Implemented, version 2.1 |

Next: snapshots and "as known at" queries, computed fields, streaming export, an MCP server over the API.

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

All from [`metrics/runs.jsonl`](metrics/runs.jsonl), the CI logs and the holdout repository, for one spec (PRP-001) and three agent runs. That is a worked example, not a benchmark. Model: `claude-fable-5-2`, Claude Code's default. Cost is the API-list-price estimate Claude Code reports; the runs were on a subscription, so it is not what was billed.

| Run | Spec | Agent | Commits | Gates | Holdout scenarios | 1M-row upload |
|-----|------|-------|---------|-------|-------------------|---------------|
| 1 | 1.3 | 34 turns, 5.6 min, $2.75 | 0 | build failed | not run | not run |
| 2 | 1.4 | 138 turns, 47.4 min, $16.68 | 7 | all pass | 496 / 496 | 46.9 s local; 34.4, 56.8, 58.0, 58.2 s on CI |
| 3 | 2.0 | 42 turns, 12.4 min, $4.84 | 2 | all pass | 509 / 509 | 7.8 s local; 9.2, 6.8 s on CI |

With the definitions fixed in [`metrics/README.md`](metrics/README.md) before the first run:

- **First-attempt success:** 2 of 3 spec versions (1.4 and 2.0). One deviation from the definition: it asks for the holdout scenarios and load gate to pass in CI on the agent's branch. CI didn't exist yet for 1.4 and isn't run on agent branches, so both ran locally on the branch's image, and CI passed afterwards on `main` with the same code.
- **Merged without human edits:** 2 of 2 merges. No human commit touches `src/`, `tests/` or `db/`. The agent wrote 44 files, 4,856 lines including tests (142 unit, 204 integration).
- **Cost of PRP-001:** $24.27 and 65 minutes of agent time over all three runs, the failed one included.
- **Memory:** peak RSS between 112.1 and 119.6 MiB in every load run, for 100k and for 1M rows (budget 300 MiB). Going from 100k to 1M rows never added more than 0.9 MiB (budget 64 MiB), so the upload streams.

### What went wrong, and what changed because of it

- **Run 1 stopped at the first task.** Claude Code ignores a project's allowed-command list in a folder that was never marked as trusted, so every `dotnet build` and `git add` was denied. The agent stopped and reported the blocker instead of writing code it couldn't build. Its PR description also listed 7 ambiguities in the spec that the holdout author had missed, one of them a real race: `now()` in PostgreSQL is the transaction start, which is before the lock is taken. All 7 went into version 1.4, and `run-prp.cs` now checks trust before it starts.
- **The first holdout run against run 2 hit two harness bugs.** First nothing ran: the newest xUnit v3 needs an opt-in to the new test runner on .NET 10, which the agent had warned about in its PR. Then 2 of 496 scenarios failed because Docker prefixed every log line with a timestamp, so the API's JSON logs didn't parse. The harness was fixed; the scenarios were not touched.
- **The throughput budget was nearly missed on CI.** Run 2 measured, without being asked, that per-row foreign key checks took 19.6 s and 19.8 s of a 1M-row merge against 4.3 s for the insert itself. CI then measured the whole upload at 34.4 to 58.2 s against a 60 s budget. Spec 2.0 dropped those foreign keys (the pipeline already guarantees both references), the holdout scenarios were updated first and failed 12 of 509 on the old code as expected, and run 3 brought the upload to 6.8 to 9.2 s on CI.
- **Writing the holdout scenarios surfaced 60 ambiguities in the spec before any code existed** (45, 10, 3 and 2 across versions 1.0 to 1.3). 58 were decided in the spec. The remaining edge cases are listed in the holdout repository with the assumption each scenario makes; 6 are open now.

Not yet measured: the CI AI review has not run, because the repository has no API key set yet, and no defects after merge have been recorded.

## Repository

```
specs/            PRPs, the template and the rules for writing them
.claude/          skills, sub-agents and permissions for Claude Code
CLAUDE.md         rules every agent reads first
tools/            verify.sh (gates), run-prp.cs (agent runs), metrics-report.cs
metrics/          runs.jsonl, written by run-prp.cs; definitions in README.md
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

The holdout scenarios are private, so a fork's CI skips them.

No secrets live in this repository. Database credentials are generated per test run, and keys come only from environment variables or CI secrets. A gitleaks hook (`git config core.hooksPath .githooks`) and the CI scan check every commit.
