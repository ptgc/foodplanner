using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
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
