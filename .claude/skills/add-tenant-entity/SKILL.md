---
name: add-tenant-entity
description: Add a household-owned entity (a new table) to the model. Use when a task adds an EF entity, or a HouseholdId to an existing one. Covers ITenantEntity, the EF configuration, the isolation-suite registration and the coverage test that fails the build without it.
---

# Add a tenant entity

Reference example: `Store` (T-087). Entity `src/Domain/ShoppingLists/Store.cs`, configuration `StoreConfiguration` in
`src/Infrastructure/Persistence/Configurations/ShoppingListConfigurations.cs`, `DbSet` in `src/Infrastructure/Persistence/AppDbContext.cs`,
isolation case in `tests/Api.Tests/TenantIsolation/TenantIsolationRegistry.cs`, migration `20261031010855_AddStores.cs`.
For a child row that points at a parent, see `RecipeStep` (`src/Domain/Recipes/`) and its case in the same registry.
The full rules are in docs/domain.md "Tenancy" (T-008, T-011, T-012, T-073). Read it first.

## Decide the kind

1. Ask: who owns a row?
   - **One household** (almost everything): `ITenantEntity` (`src/Domain/Tenancy/ITenantEntity.cs`).
   - **Global or a household** (seeded catalog rows plus a household's own): `ICatalogEntity` (`src/Domain/Catalog/`),
     like `Ingredient` and `Tag`.
   - **Nobody** (global, written only by the seed, like `Unit`): needs an entry with a reason in `TenantCoverage.Exempt`
     (`tests/Api.Tests/TenantIsolation/TenantCoverage.cs`). That is a decision: use the `raise-question` skill instead of exempting quietly.

## The entity (Domain)

2. Put the class in `src/Domain/<Feature>/`: `public sealed class Store : Entity, ITenantEntity`. `Entity`
   (`src/Domain/Common/Entity.cs`) gives the Guid `Id`.
3. `public Guid HouseholdId { get; private set; }` with **no** constructor parameter and no public setter. Infrastructure
   stamps it from `ICurrentHousehold` when the row is added. A private parameterless constructor is for EF.
4. Keep it pure: no EF or ASP.NET types (`DependencyRulesTests`). Validation and normalization live on the entity
   (`Store.NormalizeName`, `MaxNameLength`), with unit tests in `tests/Domain.Tests`.
5. Point at other rows by id (`RecipeId`, `IngredientId`), with no navigation to a filtered entity (EF warning 10622).
   Many-to-many between tenant entities needs an explicit join entity that is itself an `ITenantEntity`.

## EF configuration (Infrastructure)

6. Add an `internal sealed class XxxConfiguration : IEntityTypeConfiguration<Xxx>` in the feature's file under
   `src/Infrastructure/Persistence/Configurations/`. `ApplyConfigurationsFromAssembly` picks it up. Set: table name, max lengths,
   unique indexes that include `HouseholdId` (`HasIndex(s => new { s.HouseholdId, s.Name }).IsUnique()`), and
   `HasOne<Household>().WithMany().HasForeignKey(s => s.HouseholdId).OnDelete(DeleteBehavior.Cascade)`.
7. Only the Household FK (and parent to child inside one household) cascades. FKs to catalog rows (`Ingredient`, `Unit`)
   and a second path to the household are `Restrict`/NO ACTION; SQL Server rejects two cascade paths.
8. Every FK to a tenant or catalog entity is a single Guid column (`ReferenceKeyRulesTests`). An FK to `AspNetUsers`
   must be handled by account deletion (`AccountDeletionTests.Every_foreign_key_to_a_user_is_handled_by_the_account_deletion`
   and the table in docs/domain.md "Deleting a user account"): make it nullable and decide what deletion does.
9. Do **not** add the query filter, the concurrency token or `CreatedAt`/`UpdatedAt` yourself.
   `TenancyModelConventions.Apply` (`src/Infrastructure/Persistence/Tenancy/TenancyModelConventions.cs`) does it for every
   `ITenantEntity`: named filter `Tenant`, `HouseholdId` as concurrency token, audit columns.
   `TenantModelRulesTests.Production_model_satisfies_the_tenancy_model_rules` checks the result.
10. Add `public DbSet<Xxx> Xxxs => Set<Xxx>();` with a one-line summary to `AppDbContext`. Never add a second `DbContext`.
11. Never call `IgnoreQueryFilters`, `ExecuteUpdate`/`ExecuteDelete`, raw SQL, or `SetProperty` of `HouseholdId` in `src`.
    Only `TenantResolution.cs` may bypass the filter (`TenantBypassTests`).

## Register it in the isolation suite (CLAUDE.md rule 1)

12. Add **one** case to `TenantIsolationRegistry.Cases`:
    ```csharp
    // Stores (T-087): name unique per household, so the factory makes a fresh name each time.
    TenantIsolationCase.For<Store>(
        (_, _) => Task.FromResult(new Store("Isolation " + Guid.NewGuid().ToString("N"))),
        entry => entry.Property(s => s.SectionOrderText).CurrentValue = "Dairy,Frozen,MeatFishAndCheese,FruitAndVegetables,DryGoods,Other"),
    ```
    - The factory returns one new, unsaved row; it may ignore the household (the harness stamps it).
    - Parent rows are seeded with `await env.SaveAsync(household, parent)`, as in the `RecipeStep` case.
    - Make unique values fresh per call. `mutate` changes one persisted column, so the save is a real UPDATE.
    - Catalog entities use `TenantIsolationCase.ForCatalog<T>(...)`, which adds the global-row checks.
13. The harness (`TenantIsolationTests.cs`) then proves, against SQL Server, that another household's rows cannot be read,
    inserted, updated, deleted, moved, or bulk-changed. Limits: single Guid key, exactly one row of the type per factory call.

## The build-failing coverage check

14. `TenantIsolationCoverageTests.Every_tenant_entity_in_the_production_model_is_registered_and_Household_is_exempt`
    (`tests/Api.Tests/TenantIsolation/TenantIsolationTests.cs`) compares the production model with the registry, using
    `TenantCoverage.Problems`. It needs no database. A missing case fails with
    "Tenant entity 'Xxx' has no isolation test. Register it in TenantIsolationRegistry.Cases ...". It also fails for a catalog
    entity registered with `For`, a stale registration or exemption, and any entity that is neither a tenant entity nor on
    `Exempt`. A `HouseholdId` on a type of neither kind is caught by `TenantModelRulesTests`. Both run in the `test` step of
    `./scripts/verify.sh`, so the branch cannot be merged until the case exists.
15. The registry also feeds the household export and delete tests (`tests/Api.Tests/Households/HouseholdManagementTests.cs`
    seeds one row of every household-owned type from it), so no extra registration is needed there.

## Then

16. Add the migration with the add-migration skill (the task must be `Migration: yes`).
17. Feature tests post another household's id and call `CrossHouseholdLinks.AssertNoneAsync` (add-endpoint skill).
18. Add a short section to docs/domain.md ("Xxx (T-nnn, migration `AddXxx`)"): what the table holds, cascades, and limits.
19. Run `./scripts/verify.sh`.

## Review checklist

- `ITenantEntity` (or `ICatalogEntity`) with a private `HouseholdId` setter; nothing assigns it by hand.
- Household FK cascades; other FKs are NO ACTION; unique indexes include `HouseholdId`.
- One registry case; coverage test green; no new `Exempt` entry without an architect's answer.
- No filter bypass, bulk write or raw SQL added in `src`.
