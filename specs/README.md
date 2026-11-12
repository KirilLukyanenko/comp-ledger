# Specs

Every feature in this repository starts here. A **PRP** (Product Requirement Prompt) is a product spec written so that an AI agent can implement it without follow-up questions: what to build and why, the exact observable behaviour, the technical decisions it must not change, the traps it will fall into, and how to check its own work.

The PRP, not the code, is the source of truth. If code and PRP disagree, the code is wrong or the PRP needs a new version. Nobody silently picks one.

## Index

| PRP | Title | Status | Version |
|-----|-------|--------|---------|
| [001](001-tenant-ingestion-effective-dating.prp.md) | Multi-tenant compensation ingestion with effective dating | Draft | 1.1 |

## Who does what

| Step | Who | Sees |
|------|-----|------|
| Write the PRP | Human (an AI may help with drafting) | Everything |
| Write holdout scenarios | A separate agent session, reviewed by a human | The PRP only, never the implementation |
| Implement | Implementer agent (`/implement-prp`) | The PRP and this repository; not the holdout scenarios |
| Review | Reviewer agent, then a human | The PRP and the diff |
| Merge | Human | CI results, review, metrics |

## Lifecycle

`Draft` → `Ready` → `In progress` → `Implemented` → (`Superseded by PRP-NNN`)

- **Draft**: being written. Agents don't implement drafts.
- **Ready**: reviewed by a human; holdout scenarios for it exist.
- **In progress**: an implementation PR is open.
- **Implemented**: merged, and all gates (including holdout and load) passed on `main`.
- **Superseded**: replaced by a later PRP. Kept for history, never deleted.

## Versioning

- File name: `NNN-short-kebab-title.prp.md`. Numbers are never reused.
- Each PRP has a `Version` field and a `Changelog` table at the end.
- Wording fixes that don't change behaviour: bump the minor version (1.0 → 1.1).
- Any change to behaviour, API contract, budgets or technical decisions: bump the major version (1.x → 2.0). Holdout scenarios must be updated **before** the change is implemented.
- A budget calibrated from a real measurement gets a changelog line with the measured numbers and the CI run that produced them.

## Writing a PRP

Start from [`_template.prp.md`](_template.prp.md). A PRP is ready when:

- Every behaviour in "What" can be checked from outside (HTTP or database) without reading the code.
- Every error case has a name, a status code or error code, and an example.
- The acceptance examples cover the main path, each error class and each isolation rule, but the PRP states the rules, not only the examples.
- Out-of-scope items are listed, so the agent doesn't build them "to be helpful".
- Technical decisions that matter are fixed with a one-line reason.
- Known gotchas list what a capable engineer new to this stack would get wrong.
- Tasks are ordered, each with a checkable "Done when".
