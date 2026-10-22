---
name: finish-task
description: Wrap up a backlog task. Use when implementation is done, before asking for verification. Runs verify.sh, maps each acceptance criterion to evidence, fills in the PR template and updates the task status.
---

# Finish a task

1. **Run `./scripts/verify.sh`.** It must be green. Until task T-002 lands it intentionally fails; for tasks that depend on nothing it, say so in the PR instead of claiming green.
2. **Map every acceptance criterion to evidence.** For each criterion in the task file, name the test, command output or observed behaviour that proves it. A criterion without evidence is not done: leave its box unticked and say why.
3. **Check scope.** `git diff --stat origin/dev` should only touch files the task needs. Explain any other file in the PR.
4. **Check the rules that apply:** a new entity is in the tenant-isolation suite (CLAUDE.md rule 1), no secrets were added, docs and ADRs are updated if behaviour or a decision changed.
5. **Fill in `.github/pull_request_template.md`** as the task report (there is no PR per task, ADR 0009): task id, what changed, the criterion-to-evidence list, and the checklist. Put it in the body of your last commit on the task branch.
6. **Commit and push** your task branch. Do not push to `test` or `prod`.
7. **Update the task file:** tick only the acceptance boxes you have evidence for, set Status to `in-review`.

The verifier then checks independently and does not trust your PR text. Do not set Status to `done` yourself; the orchestrator does that after verifier and reviewer approve.
