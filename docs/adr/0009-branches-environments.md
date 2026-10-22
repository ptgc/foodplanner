# ADR 0009: Branches and environments

Status: accepted

## Context
Work is done by agents under an architect. We need isolated task work, an always-green integration branch, and human-controlled promotion to test and prod. See Q-001.

## Decision
- **Branches:** `dev` (default, integration), `test`, `prod`. No `main`.
- **Task work:** one task = one branch `task/T-xxx-slug`, created from `dev`. No PR per task. The orchestrator merges the task branch into `dev` only after CI is green and verifier and reviewer approve. CI also runs on every push to `dev`.
- **Setup and docs work** done interactively by the architect with Claude may be committed straight to `dev`.
- **Promotion:** the architect opens PRs `dev` to `test`, then `test` to `prod`. Agents never push to `test` or `prod`, never force-push, and never open or merge promotion PRs.
- **Environments:** a push to `dev` deploys the dev environment automatically. `test` and `prod` deploy from their own branch through GitHub Environments; `prod` requires human approval. Azure access uses OIDC only.
- **PR template:** the filled-in `.github/pull_request_template.md` is the task report. The agent puts it in the body of its last commit on the task branch. Promotion PRs list the task ids they include.
- **Protection:** rulesets on `test` and `prod` require a PR and block force-push and deletion (task T-064). `dev` is not protected.

## Consequences
Direct pushes to `dev` mean a bad merge breaks the integration branch, so merge gates must be strict. Promotion batches several tasks per PR, so the architect reviews a diff of the whole batch. Agents cannot enforce the branch rules alone; T-062 (settings) and T-064 (GitHub rulesets) do.
