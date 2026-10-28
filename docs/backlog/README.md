# Backlog

Status values in each task file: `todo | in-progress | blocked | in-review | done | cancelled`. Orchestrator picks `todo` tasks whose dependencies are all `done`. Only one `Migration: yes` task may be in-progress at a time.

Milestones: M0 foundations, M1 recipes, M2 planning and shopping, M3 cooking, PWA, households and environments, M4 ordering, voice and extra login, W workflow setup, L1 local first iteration.

Agent `architect` means the task is done interactively with the human and Claude; the orchestrator skips it. When a task is solved, tick its acceptance boxes and set Status to `done`.

Branch names are `task/T-xxx-` plus a lowercase kebab slug of the title, max 40 chars, no colons.

## Current focus: T-078, then T-083, T-087, T-026 (spec + Q-020 first), one at a time; backlog: T-077, T-086 (stretch), T-001

## Done: L3 local household management and kitchen use
Approved by the architect on 2026-10-25. Local only; same gates as L2 (implementer evidence + green verify.sh on dev; extra reviewer on tenancy/security changes: T-037, T-044, T-048). Order: T-074 (L2 leftover) and T-040 now; then T-037, T-044, T-048, T-045; T-041; T-050, T-051; T-042, T-043. T-001 (Oda spike) separately later.

## Done: L2 local recipes to shopping list
Decided in docs/questions/Q-010.md. Local only; every secret in .NET user secrets. Google login (T-010) postponed; a seeded local admin (T-029) is used instead, with a login screen (T-028).
Order (one migration task at a time): T-008, T-027 (parallel), T-009, T-011, T-029, T-012 and T-028 (parallel), T-020, T-031 (parallel), T-021, T-032 (parallel), T-022, T-023, T-024, T-025, T-030, T-033, T-034, T-035, T-036.
Gate: full ADR 0009 gate for T-008, T-009, T-011, T-012 (reviewer on opus, T-008 implementer on opus); implementer evidence plus a green verify.sh on dev for the rest.
Done when the seeded admin can sign in at http://localhost:3000, browse and edit recipes from the database, plan a week and see the generated shopping list.
Update 2026-10-24 (Q-011..Q-015 answered): added T-038 (translatable UI), T-039 (translatable catalog), T-072 (Q-011), T-073 (Q-012). Remaining order: T-022 (NetVips) and T-038 in parallel; T-024 (placeholder images allowed until T-022 lands) and T-039; T-025, T-035; T-072, T-073.

## Done: L1 local first iteration
Goal: the app runs on a developer machine only (no Azure): .NET API, React SPA, SQL Server in Docker, and a README with scripts for bash and PowerShell.
Scope, in this order: T-014, T-002, T-015, T-003, T-019, T-004, T-018. Do not start any other task until L1 is done, even if it looks ready. Run them one at a time because T-015, T-019 and T-018 touch the same files. T-014 comes first so build output is never committed. Done when the verifier has followed the README from a fresh clone.

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
| T-008 | EF Core base and tenancy infrastructure | M0 | T-002, T-019 | implementer-backend | yes |
| T-009 | Identity, cookie auth, local login | M0 | T-008 | implementer-backend | yes |
| T-010 | Google external login with invite round trip | M0 | T-009, T-011 | implementer-backend | no |
| T-011 | Households, memberships, roles, policies | M0 | T-009 | implementer-backend | yes |
| T-012 | Tenant isolation and architecture test suite | M0 | T-008, T-011 | verifier | no |
| T-013 | Observability baseline | M0 | T-002 | implementer-backend | no |
| T-014 | Repo hygiene: gitignore and gitattributes | M0 | - | implementer-infra | no |
| T-015 | Publish the API OpenAPI document | M0 | T-002 | implementer-backend | no |
| T-016 | Persist Data Protection keys in Azure | M0 | T-006, T-009 | implementer-infra | no |
| T-017 | Production email provider | M1 | T-009 | implementer-backend | no |
| T-018 | Local run guide, scripts and VS Code setup | L1 | T-003, T-004, T-019 | implementer-infra | no |
| T-019 | API: local SQL connectivity and readiness endpoint | L1 | T-002, T-003 | implementer-backend | no |
| T-020 | Ingredient, Unit and seed catalog | M1 | T-012 | implementer-backend | yes |
| T-021 | Recipe entities and CRUD endpoints | M1 | T-020 | implementer-backend | yes |
| T-022 | Recipe image upload and resizing | M1 | T-021 | implementer-backend | yes |
| T-023 | Tags, categories and filtering | M1 | T-021 | implementer-backend | yes |
| T-024 | Frontend: kid-friendly recipe grid and filters | M1 | T-004, T-023, T-022, T-027 | implementer-frontend | no |
| T-025 | Frontend: recipe detail and edit | M1 | T-024 | implementer-frontend | no |
| T-026 | Kid profiles and profile switching | M1 | T-011 | implementer-backend | yes |
| T-027 | Playwright e2e harness | M1 | T-003, T-004 | implementer-frontend | no |
| T-028 | Frontend: login screen and session shell | L2 | T-004, T-009, T-011, T-029 | implementer-frontend | no |
| T-029 | Local migrations and seeded admin | L2 | T-011 | implementer-backend | no |
| T-030 | MealPlan entities and endpoints | M2 | T-021 | implementer-backend | yes |
| T-031 | Domain: unit conversion and quantity merging | M2 | T-020 | implementer-backend | no |
| T-032 | Domain: shopping list generation | M2 | T-031 | implementer-backend | no |
| T-033 | Shopping list endpoints and check-off | M2 | T-030, T-032 | implementer-backend | yes |
| T-034 | SignalR sync for shopping list | M2 | T-033 | implementer-backend | no |
| T-035 | Frontend: week planner | M2 | T-024, T-030, T-027 | implementer-frontend | no |
| T-036 | Frontend: shopping list | M2 | T-033, T-034 | implementer-frontend | no |
| T-037 | Pantry memory | M2 | T-033 | implementer-backend | yes |
| T-038 | Frontend: translatable text (i18n foundation) | L2 | T-004, T-028, T-036 | implementer-frontend | no |
| T-039 | Translatable catalog data (nb + en) | L2 | T-020, T-023 | implementer-backend | yes |
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
| T-072 | Set the password at confirm time, expire unconfirmed accounts | L2 | T-009, T-028, T-038 | implementer-backend | no |
| T-073 | Save-time check for cross-household references | L2 | T-012, T-020, T-021, T-023, T-030, T-033 | implementer-backend | no |
| T-074 | Registration time as a column | L2 | T-072 | implementer-backend | yes |
| T-075 | Recipe source link | L3 | T-042, T-043 | implementer-backend | yes |
| T-076 | Ingredient amount kind, purpose and alternatives | L3 | T-075 | implementer-backend | yes |
| T-077 | AI-assisted fallback for recipe import | M4 | T-076 | implementer-backend | no |
| T-078 | Bug: not all text changes when switching language | L3 | T-038, T-039, T-041 | implementer-frontend | no |
| T-079 | Manage own ingredients: delete, merge, rename | L3 | T-020, T-037, T-042 | implementer-backend | no |
| T-080 | Bug: photo uploads over 1 MB fail with 413 through the web container | L3 | T-022, T-041 | implementer-infra | no |
| T-081 | Recipe source can be a book or a website | L3 | T-075 | implementer-backend | no |
| T-082 | Bug: recently viewed recipe no longer opens offline (PWA regression) | L3 | T-041 | implementer-frontend | no |
| T-083 | E2E tests clean up the data they create | L3 | T-027 | implementer-frontend | no |
| T-084 | Catalog: add margarine and oils | L3 | T-039, T-076 | implementer-backend | yes |
| T-085 | Shopping list sorted by store section | L3 | T-031, T-039, T-079, T-084 | implementer-backend | yes |
| T-086 | Learn the store layout from check-off order, per store (stretch) | M4 | T-085, T-087 | implementer-backend | yes |
| T-087 | Stores per household with a manual section order | M4 | T-085 | implementer-backend | yes |
| T-088 | Bug: shopping list crashes on a list cached by an older app version | L3 | T-034, T-041, T-076, T-085 | implementer-frontend | no |
| T-089 | Recipe food type (fish, chicken, beef, vegetarian, vegan ...) | M1 | T-023, T-076, T-085 | implementer-backend | yes |
| T-090 | Weekly meal mix rules (e.g. at least one fish and one vegetarian dinner) | M1 | T-089, T-026 | implementer-backend | yes |
