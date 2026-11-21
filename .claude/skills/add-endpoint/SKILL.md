---
name: add-endpoint
description: Add or change an HTTP endpoint in src/Api. Use when a task adds a route or changes a request or response type. Covers the MapXxxEndpoints file, groups and filters, authorization policies, boundary validation, ProblemDetails, OpenAPI metadata and the Api.Tests pattern.
---

# Add an endpoint

Reference example: `src/Api/Features/ShoppingLists/StoreEndpoints.cs`, tested by `tests/Api.Tests/ShoppingLists/StoreTests.cs`.
A smaller one: `src/Api/Features/Tags/TagEndpoints.cs`. Copy the nearest existing feature, then check it against the steps below.

## The endpoint file

1. **One static class per feature** in `src/Api/Features/<Feature>/<Name>Endpoints.cs`, namespace `Foodplanner.Api.Features.<Feature>`, with
   `public static IEndpointRouteBuilder MapXxxEndpoints(this IEndpointRouteBuilder api)` that returns `api`.
   Request and response types are `sealed record`s in the same file (`StoreRequest`, `StoreResponse`). Handlers are
   `private static async Task<IResult>` methods; they take the body, route values, `AppDbContext`, `ICurrentUser` or
   `ICurrentHousehold` and a `CancellationToken` as parameters.
2. **Register it** in `src/Api/Program.cs` with `api.MapXxxEndpoints();` next to the others. `api` is `app.MapApiRoot()`
   (`src/Api/Security/ApiRoot.cs`). Never map on `app` directly (only the SignalR hub does): the root group carries the
   antiforgery filter, and `EndpointPolicyTests.All_unsafe_endpoints_validate_antiforgery` fails without it.
3. **Groups.**
   - Household data: `var stores = api.MapHouseholdScopedGroup().MapGroup("/stores");`
     (`src/Api/Tenancy/HouseholdContextMiddleware.cs`). That gives `/api/households/{householdId:guid}/...`, makes
     `HouseholdContextMiddleware` fill `ICurrentHousehold` for the caller, and requires `Policies.HouseholdMember` by default.
   - Never take a household id from a body or query string. Never `AllowAnonymous` under that group (`HouseholdRouteRulesTests`).
   - Account-level routes (no household) use a plain `api.MapGroup("/account")` with a policy, as in `src/Api/Features/Auth/AccountEndpoints.cs`.
   - Anonymous routes are an allow-list in `EndpointPolicyTests.Anonymous_endpoints_match_allow_list`. A new one is an
     architecture decision: use the `raise-question` skill.
4. **Filters.** The only shared endpoint filter is antiforgery on the root group. Household resolution is middleware on
   purpose (policies run before endpoint filters), so do not add filters for auth or tenancy. Per-route metadata is fine:
   `.RequireRateLimiting(RateLimitPolicies.X)` (`src/Api/Security/RateLimitingSetup.cs`, used in `OdaEndpoints.cs`), and
   `.DisableAntiforgery()` only for a multipart upload, as in `RecipeImageEndpoints.cs` (the root filter still checks the header).

## Authorization (CLAUDE.md rule 2)

5. Attach named policies from `src/Api/Authorization/Policies.cs` with `.RequireAuthorization(...)`:
   `Policies.HouseholdAdult` for Adult or Owner writes, `Policies.HouseholdOwner` for owner-only, `Policies.FullSession` for
   account actions that kid mode must not reach. They stack on the group's `HouseholdMember`.
6. Never read `ICurrentHousehold.Role`, compare roles, or touch `ClaimsPrincipal` / `HttpContext.User` in feature code. The
   architecture tests `RoleCheckRulesTests`, `KidModeRulesTests` and `CurrentUserRulesTests.Handlers_use_ICurrentUser_only` fail.
7. A rule that depends on the resource ("the member who picked it, or an adult") is a resource policy, a handler and a small
   access service: copy `src/Api/Authorization/MealPlanEntryAccess.cs` and `Policies.MealPlanEntryOwnerOrAdult`, and register
   them in `AuthorizationSetup.AddHouseholdPolicies`.

## Validation and errors (CLAUDE.md rule 4)

8. **Validate at the boundary**, before touching the database. Request properties and the body parameter are nullable
   (`StoreRequest? request`, `string? Name`). Normalize with the domain helper (`Store.NormalizeName`), collect errors per
   camelCase field in a `Dictionary<string, string[]>`, and return `Results.ValidationProblem(errors)` (400).
9. **Ids from the client** (Q-012 rule, docs/domain.md "Tenancy"): resolve every id through the filtered `DbSet` before you
   write it. Another household's id gets the same answer as an unknown one: a `400` ValidationProblem on the field for an
   id in the body (`ChooseStore`), a `404` problem for the route's own resource (`UpdateStore`).
10. **Errors are ProblemDetails.** Use `AppProblems.Problem(status, AppProblems.StoreNotFound, "Store not found", detail)`
    from `src/Api/Problems/AppProblems.cs`. A new code is a kebab-case constant there; its `type` becomes
    `urn:foodplanner:problem:<code>`. Map known database failures on purpose and clear the change tracker:
    a unique index violation (`SqlException` 2601/2627) to `409`, `DbUpdateConcurrencyException` to the `404` or `409`
    the spec names. Do not catch `TenancyViolationException`: it is a 500 by design.
11. Success results: `Results.Ok(body)`, `Results.Created((string?)null, body)`, `Results.NoContent()`.

## OpenAPI metadata (CLAUDE.md rule 6)

12. Every route gets `.WithName("CreateStore")` (unique; it names the operation in the document and in the generated TS
    types), `.Produces<T>(status)`, `.ProducesValidationProblem()` if it can return 400, and `.ProducesProblem(status)` for every
    problem status: 403 on household routes, plus 404, 409, and 429 with rate limiting
    (`OpenApiDocumentTests.Every_rate_limited_endpoint_declares_429_with_Retry_After`).
13. `src/Api/OpenApi/OpenApiSetup.cs` already adds the group's route parameters and the default ProblemDetails response;
    do not add them by hand.
14. Regenerate and commit the contract: `./scripts/openapi.sh` (`.ps1` on Windows), then `npm --prefix web run api:generate`
    (the regenerate-api-client skill). `verify.sh` fails on a stale `openapi/openapi.v1.json`
    (`OpenApiDocumentTests.Generated_document_is_valid_and_matches_committed_file`) or a stale `web/app/api/schema.d.ts` (`web-api-check`).

## Tests in Api.Tests

15. **Fixture.** `public sealed class StoreTests(AuthDatabase db) : IClassFixture<AuthDatabase>` in `tests/Api.Tests/<Feature>/`.
    `AuthDatabase` (`tests/Api.Tests/Auth/AuthTestSupport.cs`) is one migrated database per test class on the shared SQL Server
    Testcontainer (`tests/Api.Tests/Persistence/SqlServerFixture.cs`, an assembly fixture). Its teardown fails the class if any
    test left a link between households. A container engine must be running (`verify.sh` checks `docker info`).
16. **Tenant-aware requests.** Per test: `await using var host = HouseholdHost.Create(db.ConnectionString);`
    (`tests/Api.Tests/Households/HouseholdTestSupport.cs`), `var owner = await host.SignInAsync();`,
    `var household = await owner.Client.CreateHouseholdAsync();`, other roles with `await host.JoinAsync(owner, household, "Child")`,
    and a stranger is one more `SignInAsync()` with a household of their own. The household is in the URL
    (`$"/api/households/{household}/stores"`). `ApiClient` sends the cookie and the antiforgery token itself. Read bodies with
    `JsonAsync()`; check problems with `AssertProblemAsync(HttpStatusCode.Conflict, "store-exists")`.
17. **Cover at least:**
    - the happy path of each route;
    - each validation error key (the `AssertValidationAsync` helper in StoreTests);
    - the role matrix: Child, Adult, Owner and a non-member (403), as in `Members_read_and_choose_but_only_adults_manage_and_strangers_are_refused`;
    - another household's id answered like an unknown one and left unchanged, then
      `CrossHouseholdLinks.AssertNoneAsync(context.Model, db.ConnectionString)`, as in
      `Another_households_store_cannot_be_chosen_and_an_unknown_one_gets_the_same_answer`;
    - every problem code the route returns.
18. Pure logic the handler calls goes in `src/Domain` with unit tests in `tests/Domain.Tests` (rule 3). A new entity: the
    add-tenant-entity skill. Then run `./scripts/verify.sh`.

## Review checklist

- Mapped on the root group, household routes under `MapHouseholdScopedGroup`, no household id from body or query.
- Every write has the right policy; no role, claim or `HttpContext.User` reads in `Features`.
- Every client id resolved through a filtered set; foreign and unknown ids get the same answer.
- Every error is ProblemDetails with an `AppProblems` code; metadata lists every status; document and TS types regenerated.
- Tests cover roles, a stranger, a foreign id and validation; no hand-edited `schema.d.ts`.
