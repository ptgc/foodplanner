---
name: start-task
description: Begin work on one backlog task. Use before writing any code or docs for a T-xxx task. Reads the task, spec and ADRs, checks dependencies and the migration rule, and creates the task branch.
---

# Start a task

Work on exactly one task. Do these steps in order.

1. **Read the task file** `docs/backlog/T-xxx.md`, then CLAUDE.md, docs/domain.md, the ADRs it names, and its spec if it has a `Spec:` line or one exists in `docs/specs/`.
2. **Check it is ready.**
   - Status must be `todo` (or `in-progress` if the orchestrator already set it).
   - Every task in `Depends on` must have Status `done`. If not, stop and tell the caller.
   - If `Migration: yes`, confirm no other `Migration: yes` task is `in-progress`. If one is, stop and tell the caller.
   - If the spec is missing or the acceptance criteria are not testable, use the `raise-question` skill instead of guessing.
3. **Create the branch** named in the task's `Branch:` line, from the latest `dev`:
   `git fetch origin dev && git switch -c <branch> origin/dev`
   If you were started in an isolated worktree, create the branch there.
4. **Set Status to `in-progress`** in the task file, unless the orchestrator already did.
5. **Plan the scope.** List the files you expect to touch. Anything outside the task's Goal and Out of scope needs a reason in the PR, or a question.

Never push to `test` or `prod`. Never work on `main` or `dev` directly.
