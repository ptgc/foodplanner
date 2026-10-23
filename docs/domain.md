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
- Known gaps, for T-012 to cover: `ExecuteUpdate` that sets `HouseholdId` moves rows and skips `UpdatedAt`; `Database.ExecuteSql*`/`SqlQuery` bypass filter and guard; the cached EF model holds the first context instance (and its `ICurrentHousehold`) as the filter's parameter template, so keep `ICurrentHousehold` implementations light.

## Invariants
- Merging only combines compatible units (mass with mass, volume with volume); incompatible stay separate lines.
- Scaling recipe servings scales all ingredient quantities proportionally.
- A Child profile can never perform Adult-only actions, regardless of device login.
