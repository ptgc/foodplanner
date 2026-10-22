# Backlog

Status values in each task file: `todo | in-progress | blocked | in-review | done | cancelled`. Orchestrator picks `todo` tasks whose dependencies are all `done`. Only one `Migration: yes` task may be in-progress at a time.

Milestones: M0 foundations, M1 recipes, M2 planning and shopping, M3 cooking, PWA, households and environments, M4 ordering, voice and extra login, W workflow setup.

Agent `architect` means the task is done interactively with the human and Claude; the orchestrator skips it. When a task is solved, tick its acceptance boxes and set Status to `done`.

Branch names are `task/T-xxx-` plus a lowercase kebab slug of the title, max 40 chars, no colons.

Prefer to build M0 tasks T-002 and T-008 to T-011 with architect supervision: they set the patterns agents copy.

| ID | Title | Milestone | Depends | Agent | Migration |
|---|---|---|---|---|---|
| T-001 | Oda feasibility spike | M0 | - | implementer-backend | no |
| T-002 | Solution skeleton and verify script | M0 | - | implementer-backend | no |
| T-003 | Docker and compose | M0 | T-002 | implementer-infra | no |
| T-004 | React Router SPA skeleton and API client generation | M0 | T-002, T-015 | implementer-frontend | no |
| T-005 | CI workflow | M0 | T-002, T-003, T-004 | implementer-infra | no |
| T-006 | Bicep dev environment | M0 | T-003 | implementer-infra | no |
| T-007 | CD workflow to dev | M0 | T-005, T-006 | implementer-infra | no |
| T-008 | EF Core base and tenancy infrastructure | M0 | T-002 | implementer-backend | yes |
| T-009 | Identity, cookie auth, local login | M0 | T-008 | implementer-backend | yes |
| T-010 | Google external login with invite round trip | M0 | T-009, T-011 | implementer-backend | no |
| T-011 | Households, memberships, roles, policies | M0 | T-009 | implementer-backend | yes |
| T-012 | Tenant isolation and architecture test suite | M0 | T-008, T-011 | verifier | no |
| T-013 | Observability baseline | M0 | T-002 | implementer-backend | no |
| T-014 | Repo hygiene: gitignore and gitattributes | M0 | - | implementer-infra | no |
| T-015 | Publish the API OpenAPI document | M0 | T-002 | implementer-backend | no |
| T-016 | Persist Data Protection keys in Azure | M0 | T-006, T-009 | implementer-infra | no |
| T-017 | Email sender abstraction and providers | M1 | T-009 | implementer-backend | no |
| T-020 | Ingredient, Unit and seed catalog | M1 | T-012 | implementer-backend | yes |
| T-021 | Recipe entities and CRUD endpoints | M1 | T-020 | implementer-backend | yes |
| T-022 | Recipe image upload and resizing | M1 | T-021 | implementer-backend | yes |
| T-023 | Tags, categories and filtering | M1 | T-021 | implementer-backend | yes |
| T-024 | Frontend: kid-friendly recipe grid and filters | M1 | T-004, T-023, T-022, T-027 | implementer-frontend | no |
| T-025 | Frontend: recipe detail and edit | M1 | T-024 | implementer-frontend | no |
| T-026 | Kid profiles and profile switching | M1 | T-011 | implementer-backend | yes |
| T-027 | Playwright e2e harness | M1 | T-003, T-004 | implementer-frontend | no |
| T-030 | MealPlan entities and endpoints | M2 | T-021 | implementer-backend | yes |
| T-031 | Domain: unit conversion and quantity merging | M2 | T-020 | implementer-backend | no |
| T-032 | Domain: shopping list generation | M2 | T-031 | implementer-backend | no |
| T-033 | Shopping list endpoints and check-off | M2 | T-030, T-032 | implementer-backend | yes |
| T-034 | SignalR sync for shopping list | M2 | T-033 | implementer-backend | no |
| T-035 | Frontend: week planner | M2 | T-024, T-030, T-027 | implementer-frontend | no |
| T-036 | Frontend: shopping list | M2 | T-033, T-034 | implementer-frontend | no |
| T-037 | Pantry memory | M2 | T-033 | implementer-backend | yes |
| T-040 | Frontend: cooking mode | M3 | T-025, T-032 | implementer-frontend | no |
| T-041 | Frontend: PWA install and offline shell | M3 | T-025, T-036 | implementer-frontend | no |
| T-042 | Recipe import (backend) | M3 | T-020, T-021, T-031 | implementer-backend | yes |
| T-043 | Frontend: recipe import review | M3 | T-042, T-025 | implementer-frontend | no |
| T-044 | Household management: leave, remove, delete, export (backend) | M3 | T-012, T-037 | implementer-backend | no |
| T-045 | Frontend: household settings | M3 | T-044, T-048, T-004 | implementer-frontend | no |
| T-046 | Test and prod environments and promotion CD | M3 | T-007, T-064 | implementer-infra | no |
| T-047 | Production hardening | M3 | T-046, T-013 | implementer-infra | no |
| T-048 | Household member and invite management (backend) | M3 | T-044 | implementer-backend | no |
| T-050 | IShoppingListExporter and share/copy export | M4 | T-033 | implementer-backend | no |
| T-051 | Frontend: export shopping list | M4 | T-036, T-050 | implementer-frontend | no |
| T-052 | Ingredient to product mapping | M4 | T-001, T-033 | implementer-backend | yes |
| T-053 | Oda cart integration | M4 | T-001, T-050, T-052 | implementer-backend | yes |
| T-054 | Frontend: map products and send to Oda | M4 | T-053, T-036 | implementer-frontend | no |
| T-055 | Voice in the kitchen: feasibility spike | M4 | T-040 | implementer-frontend | no |
| T-056 | Voice commands in cooking mode | M4 | T-055 | implementer-frontend | no |
| T-057 | More external login providers | M4 | T-010 | implementer-backend | no |
| T-058 | Passkey login | M4 | T-009 | implementer-backend | yes |
| T-060 | Agents: fix definitions and assign models | W | - | architect | no |
| T-061 | Process skills: start-task, finish-task, write-spec, raise-question | W | T-060 | architect | no |
| T-062 | Claude Code settings and permission guardrails | W | - | architect | no |
| T-063 | Branching and environments: ADR 0009 and doc updates | W | - | architect | no |
| T-064 | GitHub settings: branch protection and environments | W | T-063 | architect | no |
| T-065 | Skill: add-endpoint | W | T-011 | architect | no |
| T-066 | Skills: add-tenant-entity and add-migration | W | T-012 | architect | no |
| T-067 | Skills: regenerate-api-client and frontend-screen | W | T-024 | architect | no |
| T-068 | Skill: bicep-module | W | T-006 | architect | no |
| T-069 | Specs for M0 pattern-setting tasks | W | T-060 | spec-writer | no |
| T-070 | Specs for M1 tasks | W | T-012, T-069 | spec-writer | no |
| T-071 | Specs for M2 tasks | W | T-070 | spec-writer | no |
