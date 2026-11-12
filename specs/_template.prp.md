# PRP-NNN: <Feature title>

<!--
How to use: copy to specs/NNN-short-kebab-title.prp.md and fill in every section.
Delete these comments. If a section doesn't apply, write "None" and say why, rather than deleting it.
Write for an engineer who is very capable but has never seen this codebase and cannot ask you questions.
-->

| Field      | Value |
|------------|-------|
| Status     | Draft |
| Version    | 1.0 |
| Owner      | <github handle> |
| Created    | YYYY-MM-DD |
| Depends on | PRP-NNN, ... or none |
| Unlocks    | PRP-NNN, ... or none |

---

## 1. Goal

<!-- One paragraph: what exists after this PRP that didn't before, stated as observable behaviour. -->

## 2. Why

<!-- Business reason, engineering reason, scale. Two to four bullets. This is what lets the agent make sensible small decisions. -->

## 3. What

<!-- The normative part. Everything here must be checkable from outside the code. -->

### 3.1 Concepts

<!-- Domain terms with precise definitions. Ambiguous words here become bugs later. -->

### 3.2 Inputs and validation

<!-- Formats, field rules (regex, ranges, units), and what happens on each violation. -->

### 3.3 Error codes

| Code | Level | When |
|------|-------|------|

### 3.4 Semantics

<!-- Ordering, atomicity, idempotency, concurrency, what is kept and what is replaced. -->

### 3.5 API contract

| Method | Path | Tenant | Result |
|--------|------|--------|--------|

<!-- Request and response examples in JSON. State inclusive or exclusive for every range and what null means. -->

### 3.6 Non-functional budgets

<!-- Throughput, memory, latency, logging. Mark starting budgets as such and say how they get calibrated. -->

## 4. Acceptance examples (public)

<!--
Given / When / Then, with real data. Cover the main path, every error class and every isolation rule.
Remind the agent that holdout scenarios check the same rules with other data.
-->

**A1. <name>.**
Given ...
When ...
Then ...

## 5. Out of scope

<!-- Everything a helpful agent might build on its own that it must not. Point to the future PRP if there is one. -->

## 6. Context for the agent

### 6.1 Read first

<!-- Repository files and external docs, with URLs. -->

### 6.2 Current state of the repository

```
<tree of the relevant parts>
```

### 6.3 Target tree after this PRP

```
<tree with one-line comments per new file>
```

### 6.4 Technical decisions (fixed: changing one requires a PRP update first)

| Decision | Reason |
|----------|--------|

### 6.5 Known gotchas

<!-- Numbered. Each one: the trap, why it happens, what to do instead. -->

1.

## 7. Implementation blueprint

### 7.1 Data model

<!-- Normative SQL or types. -->

### 7.2 Pipeline or algorithm

<!-- Pseudocode for the non-obvious parts only. -->

### 7.3 Tasks (in order; one commit per task, message `PRP-NNN Tn: <summary>`)

| #  | Task | Done when |
|----|------|-----------|
| T1 | | |

## 8. Validation loop

```bash
dotnet build CompLedger.slnx -warnaserror
dotnet format CompLedger.slnx --verify-no-changes
dotnet test tests/CompLedger.UnitTests
dotnet test tests/CompLedger.IntegrationTests
```

<!-- Add feature-specific checks here. Restate: never weaken a test; stop and report if the PRP looks wrong. -->

## 9. Definition of done

- [ ] Every rule in section 3 is implemented and covered by a test.
- [ ] All validation levels are green on a clean checkout.
- [ ] No secrets, hostnames or credentials in the repository.
- [ ] Nothing under `specs/`, `.claude/` or `.github/` was changed.
- [ ] The PR description maps each section-3 rule to its test and pastes the validation summaries.

## 10. Anti-patterns

<!-- Specific to this feature: what a plausible but wrong implementation looks like. -->

## 11. How this PRP is checked beyond the agent's view

<!-- Holdout scenarios, load gates, AI review against the PRP, recorded metrics. -->

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.0 | YYYY-MM-DD | First version |
