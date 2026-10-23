# Domain

## Glossary
- **Household**: tenant. Owns all data except global catalog data.
- **User**: login identity (external provider or local). **Membership**: User in a Household with a role.
- **Member profile**: a person in a household; kid profiles have no credentials and are selected on a logged-in device.
- **Staple**: ingredient flagged `IsStaple` (salt, oil, flour). Never auto-added to the list; shown in a "check you have these" section.
- **Pantry item**: household memory of confirmed staples.

## Roles (per membership)
Owner: manage members, delete household. Adult: edit recipes/plans, order. Child: pick meals, check off items; no recipe edits, no orders.

## Core entities
Household, User, Membership, MemberProfile, Recipe, RecipeStep, RecipeIngredient(quantity, unit, ingredientId, note), Ingredient(IsStaple), Unit, Tag/Category, MealPlan, MealPlanEntry(date, recipe, servings, pickedBy), ShoppingList, ShoppingListItem(ingredient, mergedQuantity, unit, checked, sources), PantryItem, IngredientProductMapping(household, ingredient -> provider SKU).

## Tenancy
Shared DB/schema. `HouseholdId` on every household-owned row. Global catalog rows (ingredients, seed recipes) have `HouseholdId = null`, read-only to households; households may create their own. Enforced by EF global query filter + isolation tests.

How it is enforced (T-008, `src/Infrastructure/Persistence/Tenancy`):
- Read filter: named query filter `Tenant` on every `ITenantEntity`, from `ICurrentHousehold`; no household context means no rows. It also applies to `ExecuteUpdate`/`ExecuteDelete`.
- Write guard: a SaveChanges interceptor rejects Added/Modified/Deleted tenant rows without or outside the current household, and any change of `HouseholdId`, with `TenancyViolationException` (a 500).
- `HouseholdId` is a concurrency token: UPDATE/DELETE also match the stored `HouseholdId`, so a forged detached entity (own household, other household's row id) fails with `DbUpdateConcurrencyException`. T-013 must map that deliberately.
- `ITenantWriteScope` (only `ITenantResolution`, T-011) covers Added and Modified only. Delete inside a scope throws by design, so invites are marked used, not deleted.
- Household context (T-011, Q-004): every household-scoped endpoint lives under `/api/households/{householdId}/...` (`MapHouseholdScopedGroup`). `HouseholdContextMiddleware` (after authentication, before authorization, because policies run before endpoint filters) resolves the caller's membership once per request into `ICurrentHousehold` (`HouseholdId` and `Role`, both `null` for a non-member). Policies `HouseholdMember`/`HouseholdAdult`/`HouseholdOwner` are hierarchical and are the only readers of `Role`. "Active household" is a client choice; the server holds none.
- `Household` is the tenant root (no `HouseholdId`, not filtered). `Membership` and `Invite` are tenant entities. `ITenantResolution` (`Foodplanner.Infrastructure.Persistence.Tenancy`) is the only code that calls `IgnoreQueryFilters` or opens a write scope. Invites are marked accepted, never deleted; only the SHA-256 token hash is stored.
- Identity tables (`AspNetUsers`, claims, logins, tokens; T-009) are global identity data, not tenant entities: no `HouseholdId`, no filter. A User belongs to households only through Membership (T-011). The role tables are not created because roles are per membership.
- Isolation suite and architecture rules (T-012). Adding an `ITenantEntity` means adding one `TenantIsolationCase.For<T>(...)` line to `tests/Api.Tests/TenantIsolation/TenantIsolationRegistry.cs`; `TenantIsolationCoverageTests` fails, naming the entity, if it is missing. The harness then proves for it, against SQL Server: reads (list, Find, AsNoTracking, projection, Include) never see another household's row; insert for another household, update, delete and any `HouseholdId` change fail (`TenancyViolationException`, or `DbUpdateConcurrencyException` for a forged detached entity) and the other row is unchanged by raw SQL; `ExecuteUpdate`/`ExecuteDelete` touch only the current household. `Household` and the global identity tables are on `TenantCoverage.Exempt`, each with a reason; any other entity that is not an `ITenantEntity` fails the coverage test until it is exempted or made a tenant entity. `Architecture.Tests` enforce: `IgnoreQueryFilters` and `ITenantWriteScope.ForHousehold` only in `TenantResolution.cs` plus the documented test-support allow-list in `TenantSourceRules.Allowed` (solution-wide, src and tests; unnamed `IgnoreQueryFilters()` is never allowed); no `SetProperty` of `HouseholdId` in src; no raw SQL or raw ADO.NET in src outside Migrations; model rules (no owned `Entity`/`ITenantEntity` type, every `Entity` has `CreatedAt`/`UpdatedAt`, every tenant entity has the `Tenant` filter and the concurrency token, and a `HouseholdId` property on a non-`ITenantEntity` fails); no `AllowAnonymous`, and always a policy, under `/api/households/{householdId}` (`HouseholdRouteRulesTests`); `GetMembershipsForUser` is called only from `ListHouseholds`.
- Rule for foreign keys between tenant entities (T-021 and later). The filter and the write guard protect a row's own `HouseholdId`, not the ids it points to: a tenant row, or a many-to-many join row, can be saved pointing at another household's row, because the database only checks that the target exists. Resolve every id taken from a client through the filtered set first (B's row is then not found: answer 404) and never write the id otherwise. Feature tests post another household's id, expect the 404, and call `CrossHouseholdLinks.AssertNoneAsync` (`tests/Api.Tests/TenantIsolation`), which walks the EF model and looks in SQL for any link that crosses households. `TenancyKnownGapTests` and `CrossHouseholdLinksTests` pin the unprotected paths so nobody assumes otherwise.
- Not enforceable by a test, so a review item: a name for `HouseholdId` assembled at run time inside `SetProperty`; what `ListHouseholds` does with the roles it receives (the rule only limits who may call `GetMembershipsForUser`); an unrelated member named `ForHousehold` (the rule flags it, rename it); the cached EF model holds the first context instance (and its `ICurrentHousehold`) as the filter's parameter template, so keep `ICurrentHousehold` implementations light.

## Invariants
- Merging only combines compatible units (mass with mass, volume with volume); incompatible stay separate lines.
- Scaling recipe servings scales all ingredient quantities proportionally.
- A Child profile can never perform Adult-only actions, regardless of device login.
