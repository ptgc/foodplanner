You are the orchestrator for foodplanner, starting local iteration L1. Read CLAUDE.md first, then docs/backlog/README.md, section "Current focus: L1".

## Goal
The app runs on this machine only, with nothing in Azure: .NET API, React SPA, SQL Server in Docker, and a README with bash and PowerShell scripts so a new developer can start everything from VS Code or a terminal. Docker Desktop must not be required.

## Scope (do not start anything else, even if it looks ready)
In this order, one task at a time: T-014, T-002, T-015, T-003, T-019, T-004, T-018.
Each task file in docs/backlog lists its goal and acceptance criteria. Where a spec exists in docs/specs/ (T-002 has one), the spec and the task file together are the source of truth. The other L1 tasks have no spec: their acceptance criteria are the spec, so make them concrete before building and write down how each will be verified.
Skip T-001, T-013 and everything in M1 and later.

## Before the first task
1. Run ./scripts/doctor.sh and read its output. If dotnet (10+), node (22+), jq or a running container engine is missing, stop and tell me exactly what to install. Do not work around it.
2. Run `git status` and confirm you are on `dev` with a clean tree.

## For every task
Follow the loop in your agent definition, using the start-task and finish-task skills:
1. Set the task Status to in-progress and dispatch the named implementer with isolation: worktree.
2. When it reports back, dispatch the verifier in a fresh context. The verifier must really run things on this machine and record the evidence: `./scripts/verify.sh`, `docker compose up --build`, `curl http://localhost:3000/api/health`, `npm run build`, and so on. A criterion with no evidence stays unticked.
3. Dispatch the reviewer (skip it for tasks that only change docs). Use the default models; do not override them in this run.
4. There is no CI yet. The merge gate is a green ./scripts/verify.sh that the verifier ran itself, plus verifier PASS and reviewer approval. Then merge the task branch into dev, push dev, tick the criteria the verifier evidenced, and set Status to done.
5. If the verifier or reviewer fails the same task twice, stop and ask me instead of retrying.

## Rules that matter in this run
- Local only. No Azure, Bicep, CI/CD, or cloud resources. Never push to test or prod.
- The only credential allowed in the repo is the throwaway local SQL password in .env.example and appsettings.Development.json. `.env` itself is never committed.
- Do not apply EF migrations at app startup. There are no entities or migrations in this iteration; the database is created by the compose sql-init service.
- If a product or architecture question comes up, write docs/questions/Q-xxx.md with options and a recommendation, mark the task blocked, and continue with the next unblocked L1 task.
- Keep every script small and readable. Every command in the README must exist for both bash and PowerShell.

## Things I have not been able to verify, so report what you observe
- Whether `isolation: worktree` in the agent frontmatter was honoured (did each implementer get its own worktree?).
- Whether the skills listed in the agents' `skills:` field were loaded.
- Whether the Agent/Task tool name in your own definition worked for dispatching.
- Whether the guard hook (.claude/hooks/guard-bash.sh) blocked anything it should not have, and whether the allow rules in .claude/settings.json caused permission prompts.
If any of these failed, say which, and fix the agent definition in a docs-only follow-up rather than working around it silently. Tick the matching boxes in docs/backlog/T-060.md only when you observed the behaviour.

## When you finish
Print:
1. The status of each L1 task and the commit on dev that completed it.
2. The exact commands to start the app from a fresh clone, copied from the README, for bash and PowerShell.
3. Every deviation, failed attempt and open question, with the Q files you raised.
4. What the verifier could not check on this machine.
