---
name: verify
description: Run the deterministic quality gates (build, format, unit tests, integration tests) and report each result. Use after changing code and before saying a task is done.
allowed-tools: Bash(bash tools/verify.sh)
---

Run the gates:

```bash
bash tools/verify.sh
```

The last line of output is JSON with one result per gate: `pass`, `fail` or `skipped`.

Report:

1. A table with the four gates and their results.
2. For each failed gate, only the lines that explain the failure (compiler errors with file and line, failing test names with the assertion message). Not the whole log.
3. For each skipped gate, why it was skipped. A skipped gate is **not** a pass: say so plainly.

Don't fix anything in this skill. Report only, so the caller decides what to do next.
