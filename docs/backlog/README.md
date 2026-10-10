# Backlog

Status values in each task file: `todo | in-progress | blocked | in-review | done`. Orchestrator picks `todo` tasks whose dependencies are all `done`. Only one `Migration: yes` task may be in-progress at a time.

Prefer to build M0 tasks T-002 and T-008 to T-011 with architect supervision: they set the patterns agents copy.

| ID | Title | Milestone | Depends | Agent | Migration |
|---|---|---|---|---|---|
| T-001 | Oda feasibility spike | M0 | - | implementer-backend | no |
| T-002 | Solution skeleton and verify script | M0 | - | implementer-backend | no |
| T-003 | Docker and compose | M0 | T-002 | implementer-infra | no |
| T-004 | React Router SPA skeleton and API client generation | M0 | T-002 | implementer-frontend | no |
| T-005 | CI workflow | M0 | T-002, T-003, T-004 | implementer-infra | no |
| T-006 | Bicep dev environment | M0 | T-003 | implementer-infra | no |
| T-007 | CD workflow to dev | M0 | T-005, T-006 | implementer-infra | no |
| T-008 | EF Core base and tenancy infrastructure | M0 | T-002 | implementer-backend | yes |
| T-009 | Identity, cookie auth, local login | M0 | T-008 | implementer-backend | yes |
| T-010 | Google external login with invite round trip | M0 | T-009, T-011 | implementer-backend | no |
| T-011 | Households, memberships, roles, policies | M0 | T-009 | implementer-backend | yes |
| T-012 | Tenant isolation and architecture test suite | M0 | T-008, T-011 | verifier | no |
| T-013 | Observability baseline | M0 | T-002 | implementer-backend | no |
| T-020 | Ingredient, Unit and seed catalog | M1 | T-012 | implementer-backend | yes |
| T-021 | Recipe entities and CRUD endpoints | M1 | T-020 | implementer-backend | yes |
| T-022 | Recipe image upload and resizing | M1 | T-021 | implementer-backend | yes |
| T-023 | Tags, categories and filtering | M1 | T-021 | implementer-backend | yes |
| T-024 | Frontend: kid-friendly recipe grid and filters | M1 | T-004, T-023, T-022 | implementer-frontend | no |
| T-025 | Frontend: recipe detail and edit | M1 | T-024 | implementer-frontend | no |
| T-026 | Kid profiles and profile switching | M1 | T-011 | implementer-backend | yes |
| T-030 | MealPlan entities and endpoints | M2 | T-021 | implementer-backend | yes |
| T-031 | Domain: unit conversion and quantity merging | M2 | T-020 | implementer-backend | no |
| T-032 | Domain: shopping list generation | M2 | T-031 | implementer-backend | no |
| T-033 | Shopping list endpoints and check-off | M2 | T-030, T-032 | implementer-backend | yes |
| T-034 | SignalR sync for shopping list | M2 | T-033 | implementer-backend | no |
| T-035 | Frontend: week planner | M2 | T-024, T-030 | implementer-frontend | no |
| T-036 | Frontend: shopping list | M2 | T-033, T-034 | implementer-frontend | no |
| T-037 | Pantry memory | M2 | T-033 | implementer-backend | yes |
