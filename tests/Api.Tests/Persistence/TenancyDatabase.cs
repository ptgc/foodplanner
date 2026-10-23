using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Foodplanner.Api.Tests.Persistence;

/// <summary>
/// Class fixture: a fresh database per test class with the production migrations applied, plus the tables the
/// <see cref="TestDbContext"/> adds on top (derived from the model difference, so no hand-written DDL).
/// </summary>
public sealed class TenancyDatabase(SqlServerFixture server) : IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        ConnectionString = await server.CreateDatabaseAsync("tenancy", ct);

        await using var production = CreateProductionContext(ConnectionString);
        await production.Database.MigrateAsync(ct);

        await using var test = TestDbContext.Create(ConnectionString, household: null);
        var differences = test.GetService<IMigrationsModelDiffer>().GetDifferences(
            production.GetService<IDesignTimeModel>().Model.GetRelationalModel(),
            test.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        var commands = test.GetService<IMigrationsSqlGenerator>().Generate(differences, test.Model);
        await test.GetService<IMigrationCommandExecutor>().ExecuteNonQueryAsync(commands, test.GetService<IRelationalConnection>(), ct);
    }

    /// <summary>Fails the run if any test of the class left a link between households in the database (see CrossHouseholdLinks).</summary>
    public async ValueTask DisposeAsync()
    {
        await using var context = TestDbContext.Create(ConnectionString, household: null);
        Assert.Empty(await TenantIsolation.CrossHouseholdLinks.FindAsync(context.Model, ConnectionString));
    }

    public TestDbContext Context(Guid? household, ITenantWriteScope? writeScope = null, TimeProvider? timeProvider = null) =>
        TestDbContext.Create(ConnectionString, household, writeScope, timeProvider);

    /// <summary>Counts TestNotes with raw SQL, outside EF and its filters.</summary>
    public async Task<int> RawCountAsync(Guid household)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM TestNotes WHERE HouseholdId = @h";
        command.Parameters.AddWithValue("@h", household);
        return (int)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Reads one TestNote with raw SQL, outside EF and its filters. Null when the row does not exist.</summary>
    public async Task<(Guid HouseholdId, string Text, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)?> RawRowAsync(Guid id)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT HouseholdId, Text, CreatedAt, UpdatedAt FROM TestNotes WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            return null;
        }

        return (reader.GetGuid(0), reader.GetString(1), reader.GetDateTimeOffset(2), reader.GetDateTimeOffset(3));
    }

    public static AppDbContext CreateProductionContext(string connectionString) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options,
            new FixedHousehold(null),
            new TenantWriteScope(),
            TimeProvider.System);
}
