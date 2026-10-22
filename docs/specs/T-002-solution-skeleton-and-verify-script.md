# Spec: Solution skeleton and verify script (T-002)
## Goal
A developer or agent can clone the repo, run `./scripts/verify.sh`, and get a real build, lint and test run of an empty-but-correct .NET 10 solution. Every later backend task builds on its layout and rules.

## Users & roles
No end-user data is read or written. Roles (Owner, Adult, Child) and tenancy do not apply. The only endpoint, `/api/health`, is anonymous and returns no household data.

## Behaviour
**Contracts owned here:** solution layout, project names and reference rules, build settings, the `MapXxxEndpoints` per-feature convention, `/api/health`, and the `verify.sh` entry point.
**Contracts used from elsewhere:** none. The ProblemDetails error format is defined by T-009 (problem `type` convention) and T-013 (unhandled exceptions).

Layout (matches CLAUDE.md):
| Path | Project | References |
|---|---|---|
| `src/Domain` | `Foodplanner.Domain` (classlib) | none |
| `src/Infrastructure` | `Foodplanner.Infrastructure` (classlib) | Domain |
| `src/Api` | `Foodplanner.Api` (web) | Domain, Infrastructure |
| `tests/Domain.Tests` | `Foodplanner.Domain.Tests` | Domain |
| `tests/Api.Tests` | `Foodplanner.Api.Tests` | Api |
| `tests/Architecture.Tests` | `Foodplanner.Architecture.Tests` | Api, Domain, Infrastructure |

- The solution file is `Foodplanner.slnx` at the repo root.
- `global.json` pins a .NET 10 SDK with `rollForward: latestFeature`.
- `Directory.Build.props` applies to all projects: `Nullable=enable`, `TreatWarningsAsErrors=true`, `ImplicitUsings=enable`, `EnforceCodeStyleInBuild=true`, and `AnalysisLevel=latest`. Projects do not override these.
- `.editorconfig` holds C# style and naming rules. `dotnet format --verify-no-changes` is the lint step.
- Test framework: xUnit v3 with built-in assertions, for all three test projects. `Api.Tests` uses `WebApplicationFactory<Program>`, and `Program` is made visible to tests.
- Endpoints are organised per feature: `src/Api/Features/<Feature>/<Feature>Endpoints.cs` exposes `MapXxxEndpoints(this IEndpointRouteBuilder)`, and `Program.cs` calls one `MapXxxEndpoints` per feature. The health endpoint is the first example: `Features/Health/HealthEndpoints.cs` with `MapHealthEndpoints`.
- `GET /api/health` is a liveness check only and does not check the database. It returns 200 with `{"status":"Healthy"}` and is anonymous. The `/api` prefix is required because nginx proxies only `/api` (ADR 0003, T-003).
- `scripts/verify.sh` (bash, `set -euo pipefail`) runs, in order: `dotnet restore`, `dotnet build -c Release --no-restore`, `dotnet format --verify-no-changes`, then `dotnet test -c Release --no-build`. It exits non-zero on the first failure and prints which step failed. It is structured as named steps so T-004 (web) and later tasks can append steps without restructuring. It needs no secrets and no network beyond NuGet restore.

## Out of scope
Docker and compose (T-003), the web app and client generation (T-004), CI (T-005), EF Core and the database (T-008), auth (T-009), observability and exception-to-ProblemDetails mapping (T-013), the OpenAPI document (not assigned, see the task report), and central package management.

## Acceptance criteria (each must be testable)
- [ ] `dotnet build -c Release` on a clean clone reports `0 Warning(s)` and `0 Error(s)` (build log in the PR notes).
- [ ] Adding an unused variable to any project makes `dotnet build` fail. This proves warnings are errors; show it in the PR notes, then revert.
- [ ] `./scripts/verify.sh` exits 0 on the branch and runs restore, build, format check and all three test projects (output in the PR notes).
- [ ] `./scripts/verify.sh` exits non-zero when a test is made to fail, and when a file breaks `.editorconfig` formatting (PR notes).
- [ ] Architecture test `Domain_does_not_reference_EfCore_or_AspNetCore` fails if `Foodplanner.Domain` references any assembly starting with `Microsoft.EntityFrameworkCore` or `Microsoft.AspNetCore`.
- [ ] Architecture test `Infrastructure_does_not_reference_Api` fails if `Foodplanner.Infrastructure` references `Foodplanner.Api`.
- [ ] Api test `Health_returns_200_anonymous`: `GET /api/health` without credentials returns 200 with JSON body `status` = `Healthy`.
- [ ] `Domain.Tests` contains at least one passing test, so the project runs in `verify.sh`.

## Open questions
None.
