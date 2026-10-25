using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;

namespace Foodplanner.Api.Tests.Persistence;

public sealed class MigrationTests(SqlServerFixture server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }

    [Fact]
    public async Task Migrations_apply_to_empty_database()
    {
        var connectionString = await server.CreateDatabaseAsync("migrate", Ct);
        await using var context = TenancyDatabase.CreateProductionContext(connectionString);

        await context.Database.MigrateAsync(Ct);

        var applied = (await context.Database.GetAppliedMigrationsAsync(Ct)).ToList();
        Assert.Contains(applied, id => id.EndsWith("_Initial", StringComparison.Ordinal));
        var fromTable = await ScalarAsync(connectionString, "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId LIKE '%[_]Initial'");
        Assert.Equal(1, fromTable);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task AddIdentity_migration_applies_on_a_fresh_database()
    {
        var connectionString = await server.CreateDatabaseAsync("identity", Ct);
        await using var context = TenancyDatabase.CreateProductionContext(connectionString);

        await context.Database.MigrateAsync(Ct);

        var applied = (await context.Database.GetAppliedMigrationsAsync(Ct)).ToList();
        Assert.Contains(applied, id => id.EndsWith("_AddIdentity", StringComparison.Ordinal));
        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId LIKE '%[_]AddIdentity'"));
        foreach (var table in new[] { "AspNetUsers", "AspNetUserClaims", "AspNetUserLogins", "AspNetUserTokens" })
        {
            Assert.NotEqual(DBNull.Value, await ScalarAsync(connectionString, $"SELECT OBJECT_ID('{table}')"));
        }

        // Roles are per membership (T-011), so Identity's global role tables must not exist.
        Assert.Equal(DBNull.Value, await ScalarAsync(connectionString, "SELECT OBJECT_ID('AspNetRoles')"));
        // The user table is global: no tenant column.
        Assert.Equal(0, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('AspNetUsers') AND name = 'HouseholdId'"));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task AddHouseholds_migration_applies_on_a_fresh_database()
    {
        var connectionString = await server.CreateDatabaseAsync("households", Ct);
        await using var context = TenancyDatabase.CreateProductionContext(connectionString);

        await context.Database.MigrateAsync(Ct);

        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId LIKE '%[_]AddHouseholds'"));
        foreach (var table in new[] { "Households", "Memberships", "Invites" })
        {
            Assert.NotEqual(DBNull.Value, await ScalarAsync(connectionString, $"SELECT OBJECT_ID('{table}')"));
        }

        // Households is the tenant root: no HouseholdId. Memberships and Invites are tenant entities.
        Assert.Equal(0, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('Households') AND name = 'HouseholdId'"));
        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('Memberships') AND name = 'HouseholdId'"));
        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('Invites') AND name = 'HouseholdId'"));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task AddCatalog_migration_seeds_the_catalog_and_applying_again_changes_nothing()
    {
        var connectionString = await server.CreateDatabaseAsync("catalog", Ct);
        await using var context = TenancyDatabase.CreateProductionContext(connectionString);

        await context.Database.MigrateAsync(Ct);

        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId LIKE '%[_]AddCatalog'"));
        var ingredients = (int)(await ScalarAsync(connectionString, "SELECT COUNT(*) FROM Ingredients"))!;
        var units = (int)(await ScalarAsync(connectionString, "SELECT COUNT(*) FROM Units"))!;
        Assert.Equal(Foodplanner.Domain.Catalog.IngredientSeed.All.Count, ingredients);
        Assert.Equal(Foodplanner.Domain.Catalog.UnitSeed.All.Count, units);
        Assert.Equal(ingredients, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM Ingredients WHERE HouseholdId IS NULL"));
        Assert.InRange(ingredients, 90, 130);
        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM Ingredients WHERE Name = N'Salt' AND IsStaple = 1"));
        var fingerprint = await ScalarAsync(connectionString, "SELECT CHECKSUM_AGG(CHECKSUM(Id, Name, IsStaple, Dimension)) FROM Ingredients");

        // Idempotent: a second migrate is a no-op (the migration is recorded), counts and content are unchanged.
        await context.Database.MigrateAsync(Ct);
        await using (var again = TenancyDatabase.CreateProductionContext(connectionString))
        {
            await again.Database.MigrateAsync(Ct);
            Assert.Empty(await again.Database.GetPendingMigrationsAsync(Ct));
        }

        Assert.Equal(ingredients, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM Ingredients"));
        Assert.Equal(units, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM Units"));
        Assert.Equal(fingerprint, await ScalarAsync(connectionString, "SELECT CHECKSUM_AGG(CHECKSUM(Id, Name, IsStaple, Dimension)) FROM Ingredients"));
    }

    [Fact]
    public async Task AddCatalogTranslations_upgrades_an_existing_database_keeping_ids_and_renaming_to_english()
    {
        var connectionString = await server.CreateDatabaseAsync("translations", Ct);
        await using var context = TenancyDatabase.CreateProductionContext(connectionString);
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync("20261024200239_AddRecipeImage", Ct); // the database as it was before T-039: names in Norwegian
        var onion = Foodplanner.Domain.Catalog.IngredientSeed.All.Single(r => r.Key == "loek");
        Assert.Equal(1, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM Ingredients WHERE Id = '{onion.Id}' AND Name = N'Løk'"));
        Assert.Equal(0, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM sys.tables WHERE name = 'CatalogTranslations'"));

        await migrator.MigrateAsync(null, Ct);

        Assert.Equal(1, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM Ingredients WHERE Id = '{onion.Id}' AND Name = N'Onion'")); // same id, English name
        var expected = Foodplanner.Domain.Catalog.CatalogTranslation.SeedRows().Count();
        Assert.Equal(expected, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM CatalogTranslations"));
        Assert.Equal(1, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM CatalogTranslations WHERE Kind = 'ingredient' AND EntityId = '{onion.Id}' AND Locale = 'nb' AND Name = N'Løk'"));
        Assert.Equal(Foodplanner.Domain.Catalog.IngredientSeed.All.Count, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM Ingredients"));
        Assert.Equal(Foodplanner.Domain.Recipes.TagSeed.All.Count, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM Tags"));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task Seed_rows_are_keyed_by_fixed_ids_so_a_replay_conflicts_instead_of_duplicating()
    {
        var connectionString = await server.CreateDatabaseAsync("catalogreplay", Ct);
        await using var context = TenancyDatabase.CreateProductionContext(connectionString);
        await context.Database.MigrateAsync(Ct);
        var salt = Foodplanner.Domain.Catalog.IngredientSeed.All.Single(r => r.Key == "salt");

        // The unique name index (also among global rows) and the primary key both refuse a replayed seed row.
        var exception = await Assert.ThrowsAsync<SqlException>(() => ScalarAsync(
            connectionString,
            $"INSERT INTO Ingredients (Id, HouseholdId, Name, IsStaple, Dimension, CreatedAt, UpdatedAt) VALUES ('{salt.Id}', NULL, N'Salt', 1, 'Mass', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())"));
        Assert.Contains(exception.Number, new[] { 2601, 2627 });
        var other = await Assert.ThrowsAsync<SqlException>(() => ScalarAsync(
            connectionString,
            $"INSERT INTO Ingredients (Id, HouseholdId, Name, IsStaple, Dimension, CreatedAt, UpdatedAt) VALUES (NEWID(), NULL, N'SALT', 1, 'Mass', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())"));
        Assert.Contains(other.Number, new[] { 2601, 2627 }); // a second global "salt" under another id: the global names are unique too
        Assert.Equal(Foodplanner.Domain.Catalog.IngredientSeed.All.Count, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM Ingredients"));
    }

    [Fact]
    public async Task AddRecipes_migration_applies_and_deleting_a_household_removes_its_recipes_and_ingredients()
    {
        var connectionString = await server.CreateDatabaseAsync("recipes", Ct);
        await using var context = TenancyDatabase.CreateProductionContext(connectionString);

        await context.Database.MigrateAsync(Ct);

        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId LIKE '%[_]AddRecipes'"));
        foreach (var table in new[] { "Recipes", "RecipeSteps", "RecipeIngredients" })
        {
            Assert.Equal(1, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('{table}') AND name = 'HouseholdId'"));
        }

        // A household with its own ingredient, a recipe using it, a step and a line: one DELETE removes it all
        // (Household -> Recipe -> children cascade; the NO ACTION foreign keys are checked at the end of the statement).
        var salt = Foodplanner.Domain.Catalog.IngredientSeed.All.Single(r => r.Key == "salt");
        var gram = Foodplanner.Domain.Catalog.UnitSeed.All.First();
        await ScalarAsync(connectionString, $"""
            DECLARE @h uniqueidentifier = NEWID(), @r uniqueidentifier = NEWID(), @i uniqueidentifier = NEWID();
            DECLARE @now datetimeoffset = SYSDATETIMEOFFSET();
            INSERT INTO Households (Id, Name, CreatedAt, UpdatedAt) VALUES (@h, N'Cascade', @now, @now);
            INSERT INTO Ingredients (Id, HouseholdId, Name, IsStaple, Dimension, CreatedAt, UpdatedAt) VALUES (@i, @h, N'Own', 0, 'Mass', @now, @now);
            INSERT INTO Recipes (Id, HouseholdId, Title, Servings, CreatedAt, UpdatedAt) VALUES (@r, @h, N'R', 4, @now, @now);
            INSERT INTO RecipeSteps (Id, HouseholdId, RecipeId, Position, Text, CreatedAt, UpdatedAt) VALUES (NEWID(), @h, @r, 0, N'Stir', @now, @now);
            INSERT INTO RecipeIngredients (Id, HouseholdId, RecipeId, Position, IngredientId, Quantity, UnitId, CreatedAt, UpdatedAt) VALUES (NEWID(), @h, @r, 0, @i, 1, '{gram.Id}', @now, @now);
            INSERT INTO RecipeIngredients (Id, HouseholdId, RecipeId, Position, IngredientId, CreatedAt, UpdatedAt) VALUES (NEWID(), @h, @r, 1, '{salt.Id}', @now, @now);
            """);
        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM RecipeSteps"));

        await ScalarAsync(connectionString, "DELETE FROM Households");

        foreach (var table in new[] { "Recipes", "RecipeSteps", "RecipeIngredients" })
        {
            Assert.Equal(0, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM {table}"));
        }

        Assert.Equal(0, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM Ingredients WHERE HouseholdId IS NOT NULL"));
        Assert.Equal(Foodplanner.Domain.Catalog.IngredientSeed.All.Count, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM Ingredients"));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task AddTags_migration_applies_seeds_the_global_tags_and_deleting_a_household_removes_its_tags_and_links()
    {
        var connectionString = await server.CreateDatabaseAsync("tags", Ct);
        await using var context = TenancyDatabase.CreateProductionContext(connectionString);

        await context.Database.MigrateAsync(Ct);

        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId LIKE '%[_]AddTags'"));
        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('RecipeTags') AND name = 'HouseholdId'"));
        Assert.Equal(Foodplanner.Domain.Recipes.TagSeed.All.Count, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM Tags WHERE HouseholdId IS NULL"));
        Assert.Equal(5, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM Tags WHERE Kind = 'Category'"));

        // A second migrate is a no-op, so the seed is not duplicated.
        await context.Database.MigrateAsync(Ct);
        Assert.Equal(Foodplanner.Domain.Recipes.TagSeed.All.Count, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM Tags"));

        var seeded = Foodplanner.Domain.Recipes.TagSeed.All[0];
        await ScalarAsync(connectionString, $"""
            DECLARE @h uniqueidentifier = NEWID(), @r uniqueidentifier = NEWID(), @t uniqueidentifier = NEWID();
            DECLARE @now datetimeoffset = SYSDATETIMEOFFSET();
            INSERT INTO Households (Id, Name, CreatedAt, UpdatedAt) VALUES (@h, N'Cascade', @now, @now);
            INSERT INTO Tags (Id, HouseholdId, Name, Kind, CreatedAt, UpdatedAt) VALUES (@t, @h, N'Own', 'Tag', @now, @now);
            INSERT INTO Recipes (Id, HouseholdId, Title, Servings, CreatedAt, UpdatedAt) VALUES (@r, @h, N'R', 4, @now, @now);
            INSERT INTO RecipeTags (Id, HouseholdId, RecipeId, TagId, CreatedAt, UpdatedAt) VALUES (NEWID(), @h, @r, @t, @now, @now);
            INSERT INTO RecipeTags (Id, HouseholdId, RecipeId, TagId, CreatedAt, UpdatedAt) VALUES (NEWID(), @h, @r, '{seeded.Id}', @now, @now);
            """);
        Assert.Equal(2, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM RecipeTags"));

        await ScalarAsync(connectionString, "DELETE FROM Households");

        Assert.Equal(0, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM RecipeTags"));
        Assert.Equal(0, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM Tags WHERE HouseholdId IS NOT NULL"));
        Assert.Equal(Foodplanner.Domain.Recipes.TagSeed.All.Count, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM Tags"));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task AddMealPlans_migration_applies_enforces_one_plan_per_week_and_deleting_a_household_removes_its_plans()
    {
        var connectionString = await server.CreateDatabaseAsync("mealplans", Ct);
        await using var context = TenancyDatabase.CreateProductionContext(connectionString);

        await context.Database.MigrateAsync(Ct);

        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId LIKE '%[_]AddMealPlans'"));
        foreach (var table in new[] { "MealPlans", "MealPlanEntries" })
        {
            Assert.Equal(1, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('{table}') AND name = 'HouseholdId'"));
        }

        await ScalarAsync(connectionString, """
            DECLARE @h uniqueidentifier = NEWID(), @r uniqueidentifier = NEWID(), @p uniqueidentifier = NEWID(), @u uniqueidentifier = NEWID();
            DECLARE @now datetimeoffset = SYSDATETIMEOFFSET();
            INSERT INTO Households (Id, Name, CreatedAt, UpdatedAt) VALUES (@h, N'Cascade', @now, @now);
            INSERT INTO AspNetUsers (Id, AccessFailedCount, EmailConfirmed, LockoutEnabled, PhoneNumberConfirmed, TwoFactorEnabled) VALUES (@u, 0, 0, 0, 0, 0);
            INSERT INTO Recipes (Id, HouseholdId, Title, Servings, CreatedAt, UpdatedAt) VALUES (@r, @h, N'R', 4, @now, @now);
            INSERT INTO MealPlans (Id, HouseholdId, WeekStart, CreatedAt, UpdatedAt) VALUES (@p, @h, '2026-10-26', @now, @now);
            INSERT INTO MealPlanEntries (Id, HouseholdId, MealPlanId, Date, RecipeId, Servings, PickedByUserId, CreatedAt, UpdatedAt)
                VALUES (NEWID(), @h, @p, '2026-10-27', @r, 4, @u, @now, @now);
            """);

        // One plan per household and week.
        await Assert.ThrowsAnyAsync<Microsoft.Data.SqlClient.SqlException>(() => ScalarAsync(connectionString, """
            INSERT INTO MealPlans (Id, HouseholdId, WeekStart, CreatedAt, UpdatedAt)
                SELECT NEWID(), HouseholdId, WeekStart, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET() FROM MealPlans
            """));

        // A planned recipe cannot be deleted directly (NO ACTION), but deleting the household removes the lot.
        await Assert.ThrowsAnyAsync<Microsoft.Data.SqlClient.SqlException>(() => ScalarAsync(connectionString, "DELETE FROM Recipes"));
        await ScalarAsync(connectionString, "DELETE FROM Households");

        foreach (var table in new[] { "MealPlans", "MealPlanEntries", "Recipes" })
        {
            Assert.Equal(0, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM {table}"));
        }

        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task AddShoppingLists_migration_applies_enforces_uniqueness_and_deleting_a_household_removes_its_lists()
    {
        var connectionString = await server.CreateDatabaseAsync("shoppinglists", Ct);
        await using var context = TenancyDatabase.CreateProductionContext(connectionString);

        await context.Database.MigrateAsync(Ct);

        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId LIKE '%[_]AddShoppingLists'"));
        foreach (var table in new[] { "ShoppingLists", "ShoppingListItems", "ShoppingListItemQuantities", "ShoppingListItemSources" })
        {
            Assert.Equal(1, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('{table}') AND name = 'HouseholdId'"));
        }

        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('ShoppingListItems') AND name = 'RowVersion' AND system_type_id = TYPE_ID('timestamp')"));

        await ScalarAsync(connectionString, """
            DECLARE @h uniqueidentifier = NEWID(), @r uniqueidentifier = NEWID(), @l uniqueidentifier = NEWID(), @i uniqueidentifier = NEWID();
            DECLARE @ing uniqueidentifier = (SELECT TOP 1 Id FROM Ingredients), @unit uniqueidentifier = (SELECT TOP 1 Id FROM Units);
            DECLARE @now datetimeoffset = SYSDATETIMEOFFSET();
            INSERT INTO Households (Id, Name, CreatedAt, UpdatedAt) VALUES (@h, N'Cascade', @now, @now);
            INSERT INTO Recipes (Id, HouseholdId, Title, Servings, CreatedAt, UpdatedAt) VALUES (@r, @h, N'R', 4, @now, @now);
            INSERT INTO ShoppingLists (Id, HouseholdId, WeekStart, CreatedAt, UpdatedAt) VALUES (@l, @h, '2026-10-26', @now, @now);
            INSERT INTO ShoppingListItems (Id, HouseholdId, ShoppingListId, Section, IngredientId, Checked, QuantityChangedSinceChecked, CreatedAt, UpdatedAt)
                VALUES (@i, @h, @l, 'Main', @ing, 0, 0, @now, @now);
            INSERT INTO ShoppingListItemQuantities (Id, HouseholdId, ShoppingListItemId, Position, Amount, UnitId, CreatedAt, UpdatedAt)
                VALUES (NEWID(), @h, @i, 0, 2.5, @unit, @now, @now);
            INSERT INTO ShoppingListItemSources (Id, HouseholdId, ShoppingListItemId, RecipeId, CreatedAt, UpdatedAt)
                VALUES (NEWID(), @h, @i, @r, @now, @now);
            INSERT INTO ShoppingListItems (Id, HouseholdId, ShoppingListId, Section, Label, Checked, QuantityChangedSinceChecked, CreatedAt, UpdatedAt)
                VALUES (NEWID(), @h, @l, 'Main', N'Manual', 0, 0, @now, @now);
            """);

        // One list per household and week; one generated item per ingredient and list (manual items have no ingredient and are not limited).
        await Assert.ThrowsAnyAsync<Microsoft.Data.SqlClient.SqlException>(() => ScalarAsync(connectionString, """
            INSERT INTO ShoppingLists (Id, HouseholdId, WeekStart, CreatedAt, UpdatedAt)
                SELECT NEWID(), HouseholdId, WeekStart, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET() FROM ShoppingLists
            """));
        await Assert.ThrowsAnyAsync<Microsoft.Data.SqlClient.SqlException>(() => ScalarAsync(connectionString, """
            INSERT INTO ShoppingListItems (Id, HouseholdId, ShoppingListId, Section, IngredientId, Checked, QuantityChangedSinceChecked, CreatedAt, UpdatedAt)
                SELECT NEWID(), HouseholdId, ShoppingListId, Section, IngredientId, 0, 0, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
                FROM ShoppingListItems WHERE IngredientId IS NOT NULL
            """));
        await ScalarAsync(connectionString, """
            INSERT INTO ShoppingListItems (Id, HouseholdId, ShoppingListId, Section, Label, Checked, QuantityChangedSinceChecked, CreatedAt, UpdatedAt)
                SELECT NEWID(), HouseholdId, ShoppingListId, Section, N'Second manual', 0, 0, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
                FROM ShoppingListItems WHERE Label IS NOT NULL
            """);

        // A recipe named as a source cannot be deleted directly (NO ACTION), but deleting the household removes the lot.
        await Assert.ThrowsAnyAsync<Microsoft.Data.SqlClient.SqlException>(() => ScalarAsync(connectionString, "DELETE FROM Recipes"));
        await ScalarAsync(connectionString, "DELETE FROM Households");

        foreach (var table in new[] { "ShoppingLists", "ShoppingListItems", "ShoppingListItemQuantities", "ShoppingListItemSources", "Recipes" })
        {
            Assert.Equal(0, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM {table}"));
        }

        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task AddUserRegisteredAt_applies_on_a_fresh_database_with_a_filtered_index()
    {
        var connectionString = await server.CreateDatabaseAsync("registeredat", Ct);
        await using var context = TenancyDatabase.CreateProductionContext(connectionString);

        await context.Database.MigrateAsync(Ct);

        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId LIKE '%[_]AddUserRegisteredAt'"));
        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('AspNetUsers') AND name = 'RegisteredAt' AND system_type_id = TYPE_ID('datetimeoffset') AND is_nullable = 1"));
        Assert.Equal("([EmailConfirmed]=(0))", await ScalarAsync(connectionString, "SELECT filter_definition FROM sys.indexes WHERE object_id = OBJECT_ID('AspNetUsers') AND name = 'IX_AspNetUsers_RegisteredAt_Unconfirmed'"));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task AddUserRegisteredAt_upgrade_backfills_the_newest_claim_per_unconfirmed_user_and_removes_the_claims()
    {
        var connectionString = await server.CreateDatabaseAsync("registeredatupgrade", Ct);
        await using var context = TenancyDatabase.CreateProductionContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261024203416_AddCatalogTranslations", Ct); // before T-074: the time is a claim

        // 1: two claims (newest wins), 2: one claim, 3: unconfirmed without a claim, 4: confirmed with a stale claim,
        // 5: unconfirmed with a malformed claim plus another claim type that must survive.
        await ScalarAsync(connectionString, """
            DECLARE @u1 uniqueidentifier = NEWID(), @u2 uniqueidentifier = NEWID(), @u3 uniqueidentifier = NEWID(), @u4 uniqueidentifier = NEWID(), @u5 uniqueidentifier = NEWID();
            INSERT INTO AspNetUsers (Id, AccessFailedCount, EmailConfirmed, LockoutEnabled, PhoneNumberConfirmed, TwoFactorEnabled, UserName) VALUES
                (@u1, 0, 0, 0, 0, 0, N'one'), (@u2, 0, 0, 0, 0, 0, N'two'), (@u3, 0, 0, 0, 0, 0, N'three'), (@u4, 0, 1, 0, 0, 0, N'four'), (@u5, 0, 0, 0, 0, 0, N'five');
            INSERT INTO AspNetUserClaims (UserId, ClaimType, ClaimValue) VALUES
                (@u1, N'foodplanner:registered-at', N'000000000100'), (@u1, N'foodplanner:registered-at', N'001800000000'),
                (@u2, N'foodplanner:registered-at', N'000000086401'),
                (@u4, N'foodplanner:registered-at', N'000000000050'),
                (@u5, N'foodplanner:registered-at', N'garbage'), (@u5, N'other', N'keep');
            """);

        await migrator.MigrateAsync(null, Ct);

        // 1800000000 s = 2027-01-15T08:00:00Z, past 2038-safe arithmetic is covered by the day/second split.
        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM AspNetUsers WHERE UserName = N'one' AND RegisteredAt = '2027-01-15T08:00:00+00:00'"));
        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM AspNetUsers WHERE UserName = N'two' AND RegisteredAt = '1970-01-02T00:00:01+00:00'"));
        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM AspNetUsers WHERE UserName = N'three' AND RegisteredAt IS NULL")); // cleanup starts its window
        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM AspNetUsers WHERE UserName = N'four' AND RegisteredAt IS NULL")); // confirmed
        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM AspNetUsers WHERE UserName = N'five' AND RegisteredAt IS NULL"));
        Assert.Equal(0, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM AspNetUserClaims WHERE ClaimType = N'foodplanner:registered-at'"));
        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM AspNetUserClaims WHERE ClaimType = N'other'"));

        // Down restores a claim for the unconfirmed users that have a time.
        await migrator.MigrateAsync("20261024203416_AddCatalogTranslations", Ct);
        Assert.Equal(2, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM AspNetUserClaims WHERE ClaimType = N'foodplanner:registered-at'"));
        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM AspNetUserClaims c JOIN AspNetUsers u ON u.Id = c.UserId WHERE u.UserName = N'one' AND c.ClaimType = N'foodplanner:registered-at' AND c.ClaimValue = N'001800000000'"));
    }

    [Fact]
    public async Task No_pending_model_changes()
    {
        var connectionString = await server.CreateDatabaseAsync("pending", Ct);
        await using var context = TenancyDatabase.CreateProductionContext(connectionString);

        Assert.False(context.Database.HasPendingModelChanges(), "Model changed without a migration. Run dotnet ef migrations add.");
    }

    [Fact]
    public async Task Test_entity_is_not_in_the_production_model()
    {
        var connectionString = await server.CreateDatabaseAsync("model", Ct);
        await using var context = TenancyDatabase.CreateProductionContext(connectionString);

        Assert.Null(context.Model.FindEntityType(typeof(TestNote)));
    }

    [Fact]
    public async Task Api_startup_does_not_migrate()
    {
        var connectionString = await server.CreateDatabaseAsync("startup", Ct);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString })));
        using var client = factory.CreateClient();

        // Readiness opens a real connection, so the host has started and touched the database.
        using var response = await client.GetAsync("/api/health/ready", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(DBNull.Value, await ScalarAsync(connectionString, "SELECT OBJECT_ID('__EFMigrationsHistory')"));
    }
}
