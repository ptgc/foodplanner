using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Foodplanner.Api.Tests.TenantIsolation;

/// <summary>
/// BACKSTOP AND TEST HELPER (since T-073 the save guard refuses a new or changed foreign key to another household's row, so
/// this walks the database for links that got in some other way: raw SQL, a migration, a bug in the guard). The rule
/// below still holds for handlers: they answer 400/404 first, the save guard is only the last line.
/// RULE FOR EVERY FEATURE THAT ADDS A FOREIGN KEY BETWEEN TENANT ENTITIES (T-021 recipes, T-022 planner, T-024 list
/// and later): the query filter and the household write guard protect a row's own HouseholdId; the reference check of
/// the save guard (TenancyReferenceCheck, T-073) protects the keys it points at. Before T-073 a
/// tenant row could be saved with a foreign key to another household's row, because the database only checks that the
/// target exists. The save guard covers explicit join entities (ITenantEntity); an implicit (shared-type) many-to-many
/// join is not seen by it, which is why the model rules forbid one between tenant entities.
/// <list type="number">
/// <item>Resolve every referenced id through the filtered set first (<c>db.Set&lt;T&gt;().FindAsync</c> or a filtered
/// query). From household A, B's row is simply not found, so answer 404 and never write the id.</item>
/// <item>In the feature's tests, add a case that posts another household's id and expect that 404 (or 422), then call
/// <see cref="AssertNoneAsync"/> so a missed check is a failing test.</item>
/// <item>Never accept a parent id for a child from a client without that lookup, and never use
/// <c>IgnoreQueryFilters</c> to find it (the architecture tests forbid it).</item>
/// </list>
/// The check below needs no per-entity code: it walks the EF model and, in SQL, looks for any row whose foreign key
/// points to a row of a different household. It covers foreign keys between two tenant entities and the join rows
/// of a many-to-many between two tenant entities.
/// </summary>
public static class CrossHouseholdLinks
{
    /// <summary>Descriptions of every link that crosses households, as the database holds it. Empty when sound.</summary>
    public static async Task<List<string>> FindAsync(IModel model, string connectionString)
    {
        var violations = new List<string>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        foreach (var (description, sql) in Queries(model))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var count = (int)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            if (count > 0)
            {
                violations.Add($"{description}: {count} row(s) point to another household's row");
            }
        }

        return violations;
    }

    public static async Task AssertNoneAsync(IModel model, string connectionString) =>
        Assert.Empty(await FindAsync(model, connectionString));

    /// <summary>The SQL checks the model implies. Exposed so a test can prove the model yields checks at all.</summary>
    public static List<(string Description, string Sql)> Queries(IModel model)
    {
        var queries = new List<(string, string)>();
        foreach (var dependent in model.GetEntityTypes().Where(e => !e.IsOwned() && e.GetTableName() is not null))
        {
            var dependentIsTenant = IsScoped(dependent.ClrType);
            var tenantForeignKeys = dependent.GetDeclaredForeignKeys()
                .Where(fk => IsScoped(fk.PrincipalEntityType.ClrType)
                    && fk.PrincipalEntityType.GetTableName() is not null
                    && fk.Properties.All(p => p.Name != nameof(ITenantEntity.HouseholdId)))
                .ToList();

            if (dependentIsTenant)
            {
                foreach (var fk in tenantForeignKeys)
                {
                    queries.Add((
                        $"{dependent.DisplayName()}.{string.Join("+", fk.Properties.Select(p => p.Name))} -> {fk.PrincipalEntityType.DisplayName()}",
                        $"SELECT COUNT(*) FROM {Table(dependent)} c JOIN {Table(fk.PrincipalEntityType)} p ON {On(fk, "c", "p")} WHERE p.[HouseholdId] IS NOT NULL AND (c.[HouseholdId] IS NULL OR c.[HouseholdId] <> p.[HouseholdId])"));
                }
            }
            else
            {
                // A join entity (or any non-tenant row) referencing two tenant rows: both must be the same household.
                for (var i = 0; i < tenantForeignKeys.Count; i++)
                {
                    for (var j = i + 1; j < tenantForeignKeys.Count; j++)
                    {
                        var (first, second) = (tenantForeignKeys[i], tenantForeignKeys[j]);
                        queries.Add((
                            $"{dependent.DisplayName()} joins {first.PrincipalEntityType.DisplayName()} and {second.PrincipalEntityType.DisplayName()}",
                            $"SELECT COUNT(*) FROM {Table(dependent)} c JOIN {Table(first.PrincipalEntityType)} p1 ON {On(first, "c", "p1")} "
                            + $"JOIN {Table(second.PrincipalEntityType)} p2 ON {On(second, "c", "p2")} WHERE p1.[HouseholdId] <> p2.[HouseholdId]"));
                    }
                }
            }
        }

        return queries;
    }

    /// <summary>
    /// A tenant entity, or a catalog entity (T-020). A catalog row with a NULL household is global: pointing at it is
    /// allowed from anywhere (so a NULL principal is never a violation), but a global row pointing at a household's row
    /// is one, and so is a household row pointing at another household's.
    /// </summary>
    private static bool IsScoped(Type clrType) =>
        typeof(ITenantEntity).IsAssignableFrom(clrType) || typeof(ICatalogEntity).IsAssignableFrom(clrType);

    private static string Table(IEntityType entityType) => $"[{entityType.GetSchema() ?? "dbo"}].[{entityType.GetTableName()}]";

    private static string On(IForeignKey fk, string dependentAlias, string principalAlias)
    {
        var dependent = StoreObjectIdentifier.Table(fk.DeclaringEntityType.GetTableName()!, fk.DeclaringEntityType.GetSchema());
        var principal = StoreObjectIdentifier.Table(fk.PrincipalEntityType.GetTableName()!, fk.PrincipalEntityType.GetSchema());
        return string.Join(
            " AND ",
            fk.Properties.Zip(fk.PrincipalKey.Properties, (d, p) =>
                $"{dependentAlias}.[{d.GetColumnName(dependent)}] = {principalAlias}.[{p.GetColumnName(principal)}]"));
    }
}
