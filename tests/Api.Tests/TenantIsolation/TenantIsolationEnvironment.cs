using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Identity;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Foodplanner.Api.Tests.TenantIsolation;

/// <summary>
/// What a tenant-isolation case needs from the database: production <see cref="AppDbContext"/>s scoped to a
/// household, rows for the parents every tenant row needs (households, users), and a raw-SQL snapshot of a row that
/// bypasses EF and its filters, so "unchanged" is checked against the database, not against EF's view of it.
/// </summary>
public sealed class TenantIsolationEnvironment(string connectionString, Func<string, Guid?, AppDbContext>? contextFactory = null)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public string ConnectionString { get; } = connectionString;

    /// <summary>
    /// The production model, built without connecting. Used by the coverage tests, so it must never depend on a
    /// database.
    /// </summary>
    public static IModel ProductionModel()
    {
        using var context = TenancyDatabase.CreateProductionContext("Server=unused;Database=unused");
        return context.Model;
    }

    /// <summary>
    /// A context acting for <paramref name="household"/> (<c>null</c>: no household context). The production
    /// <see cref="AppDbContext"/> unless the environment was given a factory (self-tests of the harness use test contexts).
    /// </summary>
    public AppDbContext ContextFor(Guid? household) =>
        contextFactory?.Invoke(ConnectionString, household) ?? new(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options,
            new FixedHousehold(household),
            new TenantWriteScope(),
            TimeProvider.System);

    /// <summary>Saves <paramref name="entity"/> through a context of <paramref name="household"/>. For seeding the parent rows a child entity needs.</summary>
    public async Task SaveAsync(Guid household, object entity)
    {
        await using var context = ContextFor(household);
        context.Add(entity);
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>Creates a household row (the tenant root, unfiltered) and returns its id.</summary>
    public async Task<Guid> NewHouseholdAsync()
    {
        await using var context = ContextFor(null);
        var household = new Household("Isolation " + Guid.NewGuid().ToString("N")[..8]);
        context.Households.Add(household);
        await context.SaveChangesAsync(Ct);
        return household.Id;
    }

    /// <summary>Creates a login identity row (global, not tenant data) and returns its id.</summary>
    public async Task<Guid> NewUserAsync()
    {
        await using var context = ContextFor(null);
        var id = Guid.NewGuid();
        var email = $"isolation-{id:N}@example.com";
        context.Users.Add(new AppUser
        {
            Id = id,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString(),
        });
        await context.SaveChangesAsync(Ct);
        return id;
    }

    /// <summary>
    /// Makes a saved catalog row global (HouseholdId NULL) with raw SQL. Request code cannot do this, by design; in
    /// production the seed (migration data) creates global rows. The tests use it to stand in for a seeded row.
    /// </summary>
    public async Task MakeGlobalAsync(Type entityType, Guid id)
    {
        await using var modelContext = ContextFor(null);
        var model = modelContext.Model.FindEntityType(entityType)
            ?? throw new InvalidOperationException($"{entityType.Name} is not in the model of this environment.");
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE [{model.GetSchema() ?? "dbo"}].[{model.GetTableName()}] SET HouseholdId = NULL WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
    }

    /// <summary>
    /// Every column of the row, read with raw SQL outside EF and its filters, as one comparable string.
    /// <c>null</c> when the row does not exist.
    /// </summary>
    public async Task<string?> SnapshotAsync(Type entityType, Guid id)
    {
        await using var modelContext = ContextFor(null);
        var model = modelContext.Model.FindEntityType(entityType)
            ?? throw new InvalidOperationException($"{entityType.Name} is not in the model of this environment.");
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM [{model.GetSchema() ?? "dbo"}].[{model.GetTableName()}] WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        if (!await reader.ReadAsync(Ct))
        {
            return null;
        }

        return string.Join(
            " | ",
            Enumerable.Range(0, reader.FieldCount).Select(i => $"{reader.GetName(i)}={Format(reader.GetValue(i))}"));
    }

    private static string Format(object value) => value switch
    {
        DBNull => "NULL",
        byte[] bytes => Convert.ToHexString(bytes),
        DateTimeOffset moment => moment.ToString("O"),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
