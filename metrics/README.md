# Metrics

Every number in the project README comes from the files in this folder. They are written by tools, not by hand, and never edited afterwards. A wrong record is corrected by appending a new record that says so.

## Files

| File | Written by | One line per |
|------|-----------|--------------|
| `runs.jsonl` | `tools/run-prp.cs` | Agent run (one attempt at implementing a PRP) |
| `prs.jsonl`  | CI, after merge (added later) | Merged PR: its commits, gates, review result |
| `defects.jsonl` | By hand through `tools/` (added later) | Defect found after merge, linked to a PRP |

### `runs.jsonl` fields

| Field | Meaning |
|-------|---------|
| `runId` | Unique id of the run. The agent puts it in the `Agent-Run:` trailer of every commit it makes |
| `prp`, `prpVersion` | Which spec and which version of it |
| `attempt` | 1 for the first run of this PRP version, 2 for the next, and so on |
| `branch`, `baseCommit`, `headCommit` | Where the run started and ended |
| `startedAt`, `finishedAt` | UTC timestamps |
| `agent` | What Claude Code reported: `isError`, `subtype`, `numTurns`, `durationMs`, `durationApiMs`, `costUsd`, `usage` (tokens, cache included), `modelUsage` (per model) |
| `gates` | Results of `tools/verify.sh`, run by the wrapper **after** the agent finished (not by the agent): `build`, `format`, `unit`, `integration`, each `pass`, `fail` or `skipped` |
| `humanInterventions` | Prompts a human sent during the run. Always 0 for headless runs |

## Definitions

These are fixed before the first run so the numbers can't be shaped after the fact.

- **First-pass success.** A PRP version counts as first-pass if its `attempt: 1` run ended without an agent error, all local gates passed, and the holdout scenarios and load gate in CI passed on the resulting branch, with no human commits in between. Reported as "k of n PRPs", not as a percentage, while n is small.
- **Merged without human edits.** A merged PR where every commit carries an `Agent-Run:` trailer. A human commit (fix, rebase conflict resolution, test change) makes the PR count as edited. Squash merges are not used, so the commits stay visible.
- **Cost per feature.** The sum of `agent.costUsd` over every run of the PRP (all attempts, the review included). This is the API-price equivalent that Claude Code computes from token usage. On a subscription plan it is not the money actually paid, and the README says so.
- **Wall time per feature.** The sum of run durations. Human review time is not included and is reported separately if measured.
  - Clarified after run `prp002-20261113T220528Z-69ed45b3`: a run's duration is `finishedAt - startedAt`, which `run-prp.cs` takes right around the agent process. Claude Code's `durationMs` and `numTurns` can cover only the last part of a session that waited for background work: that run reported 1.5 min and 6 turns for a process that ran 27.9 min. For the earlier runs the two durations agree within 0.1 min. `tools/metrics-report.cs` marks such turn counts.
- **Defect leakage.** Defects found after merge (by a later PRP's holdout scenarios, by load gates or by hand) per merged PRP. Each defect is linked to the PRP whose rule it breaks.

## What is not claimed

- No numbers from runs that are not in these files.
- No extrapolation from a handful of features to a percentage.
- Failed and abandoned runs stay in the log and count toward cost.
