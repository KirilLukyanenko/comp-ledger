---
name: implement-prp
description: Implement one PRP from specs/ end to end - tasks in order, one commit per task, quality gates, then an independent review by the reviewer sub-agent. Run only when a human asks for a specific PRP number.
argument-hint: <prp-number> [run-id]
disable-model-invocation: true
---

You are implementing PRP **$0**. Run id: `$1` (if this is literally `$1` or empty, there is no run id).

## 1. Find and check the spec

- The spec is the one file matching `specs/$0-*.prp.md`. If there is no match or more than one, stop and report.
- Read `CLAUDE.md`, `specs/README.md` and the whole PRP, top to bottom, before writing code. Open the "Read first" links when you need them.
- If the PRP's `Status` is not `Ready`, stop and report. Drafts are not implemented.
- Compare the repository with the PRP's "Current state" tree. If it differs in a way that matters, stop and report rather than adapting the PRP yourself.

## 2. Implement, task by task

For each task in the PRP's "Tasks" table, in order:

1. Implement it, and only it.
2. Run what its "Done when" column asks for (use `/verify` or the individual `dotnet` commands).
3. Commit with the message `PRP-$0 Tn: <imperative summary>` and these trailers:

   ```bash
   git commit -m "PRP-$0 T1: Add solution skeleton" --trailer "Spec: PRP-$0" --trailer "Agent-Run: $1"
   ```

   If there is no run id, omit the `Agent-Run` trailer. Never invent one.

If the same gate still fails after three honest attempts at a fix, stop. Commit what is consistent, and report what fails and why. Don't weaken tests, and don't work around the PRP.

## 3. Verify everything

Run `bash tools/verify.sh` on the final state. Every gate must be `pass`. A gate skipped because Docker is unavailable is reported as **not verified**, never as passed.

## 4. Independent review

Start the `reviewer` sub-agent. Give it the PRP path and the base commit (`git merge-base HEAD main`). For each finding it marks as blocking:

- If it is a real gap or violation of the PRP, fix it, rerun the gates, and commit as `PRP-$0 fix: <summary>` with the same trailers.
- If you disagree, keep the code and explain why in the PR description.

Run the reviewer once more after fixes. Don't loop beyond that: remaining disagreements go to the human.

## 5. PR description

Write `.prp-run/pr-description.md` (this folder is gitignored) with:

- A table: every rule in the PRP's section 3, then the test that covers it (file and test name).
- The gate results from `tools/verify.sh`.
- The reviewer's final verdict and any disagreements.
- **Spec gaps**: everything in the PRP that was ambiguous, contradictory or impossible, and what you did about it.

## 6. Final message

Summarise: tasks done, gates, review verdict, spec gaps. Don't push, don't open a PR, don't change protected paths (see `CLAUDE.md`).
