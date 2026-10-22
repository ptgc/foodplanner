# foodplanner — agent instructions

Mobile-first web app for families: pick weekly meals (image-driven, kid friendly), auto-generate a shopping list, cook from recipes, later order via Oda and use voice in the kitchen. Multi-household from day one.

## Source of truth (read before working)
- `docs/adr/` settled decisions. Do NOT re-litigate. If you think one is wrong, write a question.
- `docs/domain.md` glossary + data model + tenancy rule.
- `docs/specs/` feature behaviour. `docs/backlog/` tasks. `docs/questions/` escalations to the architect.

## Stack
.NET 10 (LTS), ASP.NET Core **Minimal APIs**, EF Core **code-first** (Azure SQL / SQL Server), React Router v7 **SPA mode** + TypeScript strict, SignalR, Docker everywhere, Azure Container Apps, **Bicep** IaC, GitHub Actions (OIDC to Azure).

## Layout
```
/infra               Bicep modules + .bicepparam per env
/src/Api             Host, endpoints (one MapXxxEndpoints per feature), DI wiring
/src/Domain          Pure logic. NO references to EF Core or ASP.NET.
/src/Infrastructure  EF Core, Blob, Oda client, Speech, email
/web                 React Router app (nginx container in prod, same-origin /api proxy)
/tests               Domain.Tests, Api.Tests (Testcontainers SQL Server), Architecture.Tests, e2e (Playwright)
```

## Commands (created by tasks T-002/T-003/T-005; keep them working)
- `./scripts/verify.sh` — build, lint, unit, integration, architecture tests. CI runs the same. **Must be green before a task branch is merged.**
- `docker compose up` — SQL Server, Azurite, api, web for local dev.
- `dotnet ef` via migration bundles; never apply migrations at app startup.

## Hard rules
1. **Tenancy:** every household-owned entity implements `ITenantEntity` (`HouseholdId`). A global EF query filter driven by `ICurrentHousehold` enforces it. Every new entity must be added to the tenant-isolation test suite.
2. Authorization is policy-based (roles per membership: Owner, Adult, Child). No scattered role-string checks.
3. Domain logic (units, scaling, merging, list generation) is pure, in `Domain`, with thorough unit tests.
4. Nullable enabled, warnings as errors. Errors returned as RFC 9457 ProblemDetails. Validate at the boundary.
5. Ingredients are structured: quantity + unit + ingredient id. Never free text in the model (keep original text only as an import artifact).
6. API contract = OpenAPI; the TS client is generated, never hand-written.
7. No secrets in repo/workflows. Key Vault + managed identity. Never deploy to prod; the prod GitHub Environment needs human approval.
8. One task = one branch (`task/T-xxx-slug`) created from `dev`. The orchestrator merges it into `dev` after CI, verifier and reviewer pass (ADR 0009). Never push to `test` or `prod`; the architect promotes `dev` to `test` to `prod` by PR. Don't touch files outside the task scope; if you must, say why in the task report.
9. Migrations: only one task at a time may add a migration (check backlog `Migration: yes`).
10. Mobile-first: design at 390px, touch targets >= 44px.

## When unsure
Do not guess on product or architecture. Create `docs/questions/Q-xxx.md` (use the template) with 2-3 options and a recommendation, mark the task `blocked`, move on.

## Definition of done
Acceptance criteria met, `verify.sh` green, tests added, docs/ADRs updated if behaviour or decisions changed, task report (PR template) filled in.
