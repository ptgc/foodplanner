---
name: write-spec
description: Write a testable feature spec for a backlog task in docs/specs. Use when a task has no spec or its acceptance criteria are vague.
---

# Write a spec

1. Read the task file, CLAUDE.md, docs/domain.md and the relevant ADRs.
2. Copy `docs/specs/_template.md` to `docs/specs/T-xxx-slug.md` (slug matches the task branch slug).
3. Fill every section:
   - **Goal:** one or two sentences from the user's point of view.
   - **Users & roles:** what Owner, Adult and Child can and cannot do. Never leave this blank when data is read or written.
   - **Behaviour:** concrete rules, including empty states, errors (ProblemDetails) and tenancy. Another household's data is never visible.
   - **Out of scope:** match the task file.
   - **Acceptance criteria:** each one names an observable result or the test that proves it. Rewrite any criterion you could not turn into a test.
   - **Open questions:** one line per question, linking its `Q-xxx` file.
4. Do not decide product or architecture questions. Use `raise-question` for each one.
5. Add a `Spec: docs/specs/T-xxx-slug.md` line to the task file header.

A good criterion: "A Child profile calling POST /api/recipes receives 403 ProblemDetails." A bad one: "Permissions work correctly."
