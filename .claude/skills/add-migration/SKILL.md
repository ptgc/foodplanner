---
name: add-migration
description: Create an EF Core migration for a model change. Use when a task changes an entity, column, index or seed data. Covers the backlog Migration flag and the one-migration-at-a-time rule, the dotnet ef command, the tests that check it, the bundle step and why the app never migrates at startup.
---

# Add a migration

Reference example: `src/Infrastructure/Persistence/Migrations/20261031010855_AddStores.cs` (with its `.Designer.cs` and the
updated `AppDbContextModelSnapshot.cs`), from task T-087 (`Migration: yes`). Its entity work is in the add-tenant-entity skill.

## Before you start (CLAUDE.md rule 9)

1. **Check the flag.** The task file header must say `Migration: yes`. If it says `no` and your change needs a migration,
   stop and use the `raise-question` skill. Do not add one quietly.
2. **Check no other migration task is in progress.** On a fresh branch from the latest `dev` (start-task skill), run:
   `grep -l '^Migration: yes' docs/backlog/T-*.md | xargs grep -l '^Status: in-progress'`
   It must list only your own task. The `Migration` column in `docs/backlog/README.md` shows the same flag.
   If another task is listed, stop and tell the caller. A migration task in `in-review` is not merged yet either: say so too,
   because both migrations would rewrite the model snapshot.

## Create it

3. Change the model first (entity, `IEntityTypeConfiguration`, `DbSet`) and build: `dotnet build Foodplanner.slnx`.
4. Create the migration, from the repo root:
   ```bash
   dotnet tool restore
   dotnet ef migrations add AddXxx --project src/Infrastructure --startup-project src/Api --output-dir Persistence/Migrations
   ```
   `dotnet-ef` is pinned in `dotnet-tools.json`. The command uses `AppDbContextDesignTimeFactory`
   (`src/Infrastructure/Persistence/AppDbContextDesignTimeFactory.cs`), so it needs no database and never starts the Api host.
   Name it like the others: PascalCase verb and noun (`AddStores`, `AddPantryAmounts`, `MakeUserLinksNullable`).
5. Commit all three files: `<timestamp>_AddXxx.cs`, `<timestamp>_AddXxx.Designer.cs` and `AppDbContextModelSnapshot.cs`.
   Never hand-edit the Designer file or the snapshot.
6. **Read the generated `Up` and `Down`.** Check:
   - the `HouseholdId` FK is `Cascade` and the others are `NoAction`/`Restrict`;
   - unique indexes include `HouseholdId`;
   - `CreatedAt`/`UpdatedAt` are there (the tenancy conventions add them).
   If something is wrong, fix the model and regenerate:
   `dotnet ef migrations remove --project src/Infrastructure --startup-project src/Api`, then add it again.
   Never edit or remove a migration that is already on `dev`; write a new one.
7. **Data changes.** Moving or backfilling data goes in `Up` with `migrationBuilder.Sql(...)`. Raw SQL is allowed only under
   `Migrations` (`TenantBypassTests.Raw_SQL_is_not_used_in_production_code_outside_migrations`). Global seed rows come from the
   Domain seed files through `HasData` with fixed ids (`CatalogSeedData` in
   `src/Infrastructure/Persistence/Configurations/CatalogConfigurations.cs`, data in `src/Domain/Catalog/IngredientSeed.cs`),
   never from request code. If `Down` cannot restore the old state, say so in docs/domain.md, as for `MakeUserLinksNullable`.

## Tests

8. These already run against every migration (`tests/Api.Tests/Persistence/MigrationTests.cs`):
   - `No_pending_model_changes` fails if the model changed without a migration;
   - `Migrations_apply_to_empty_database` applies the full chain;
   - every Api test class migrates its own database (`AuthDatabase` in `tests/Api.Tests/Auth/AuthTestSupport.cs`), so all
     feature tests run on your schema.
9. Add a test for what the migration decides:
   - deleting a household removes the new rows (`StoreTests.Deleting_a_household_whose_members_chose_a_store_leaves_nothing_behind`,
     or `MigrationTests.AddRecipes_migration_applies_and_deleting_a_household_removes_its_recipes_and_ingredients`);
   - a backfill upgrades existing rows correctly
     (`MigrationTests.AddRecipeSource_backfills_recipes_saved_from_an_import_and_leaves_the_rest`).

## Apply it, never at startup

10. **Never apply migrations at app startup.** No `Database.Migrate`, `MigrateAsync` or `EnsureCreated` in `src`.
    `MigrationTests.Api_startup_does_not_migrate` starts the Api against an empty database and fails if the
    `__EFMigrationsHistory` table appears.
11. **Locally**, apply it explicitly: `./scripts/local-migrate.sh` (`.ps1` on Windows). It runs `dotnet ef database update`
    against the compose database; `local-up` runs it too. Do this before e2e tests.
12. **Deployed environments** run a migration bundle as a pipeline step, before the new revision gets traffic (ADR 0002,
    ADR 0006). That pipeline is tasks T-007 and T-046, still `todo`: there is no bundle script or workflow yet. Do not add one
    in a feature task. Keep the migration bundle-safe:
    - it may use only the connection string (the bundle runs the design-time factory: no app services, settings or secrets);
    - the old app revision briefly runs on the new schema, so prefer additive changes (new table, nullable column) and say so
      in the task report when a change is not additive.
    Optional local check that it bundles (do not commit the output):
    `dotnet ef migrations bundle --project src/Infrastructure --startup-project src/Api --output <temp dir>/efbundle --force`

## Finish

13. Name the migration in the docs/domain.md section of the feature ("(T-087, ..., migration `AddStores`)") and in the task report.
14. Run `./scripts/verify.sh`.

## Review checklist

- The task is `Migration: yes`, and no other migration task was in progress.
- Three files, generated not hand-written; cascades and indexes as intended; data moves tested.
- Nothing in `src` migrates at startup; no bundle or workflow changes outside T-007/T-046.
