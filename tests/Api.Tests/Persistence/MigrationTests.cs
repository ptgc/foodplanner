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
