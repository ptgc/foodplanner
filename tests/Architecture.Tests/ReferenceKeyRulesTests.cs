using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Architecture.Tests;

/// <summary>
/// The save-time reference check (T-073) supports single-column Guid foreign keys to tenant or catalog principals.
/// Anything else would throw at the first write; this fails in CI instead.
/// </summary>
public class ReferenceKeyRulesTests
{
    [Fact]
    public void Every_foreign_key_to_a_tenant_or_catalog_entity_is_a_single_Guid_column()
    {
        using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlServer("Server=unused;Database=unused").Options,
            new NoHousehold(), new NoScope(), TimeProvider.System);

        var checkedKeys = 0;
        var problems = new List<string>();
        foreach (var entityType in context.Model.GetEntityTypes().Where(e => IsScoped(e.ClrType)))
        {
            foreach (var fk in entityType.GetDeclaredForeignKeys().Where(fk => IsScoped(fk.PrincipalEntityType.ClrType)))
            {
                checkedKeys++;
                if (fk.Properties.Count != 1 || fk.PrincipalKey.Properties.Count != 1 || fk.PrincipalKey.Properties[0].ClrType != typeof(Guid))
                {
                    problems.Add($"{entityType.ClrType.Name} -> {fk.PrincipalEntityType.ClrType.Name} is composite or not a Guid key");
                }
            }
        }

        Assert.True(checkedKeys > 5); // not vacuous
        Assert.Empty(problems);
    }

    private static bool IsScoped(Type type) =>
        typeof(ITenantEntity).IsAssignableFrom(type) || typeof(ICatalogEntity).IsAssignableFrom(type);

    private sealed class NoHousehold : ICurrentHousehold
    {
        public Guid? HouseholdId => null;

        public HouseholdRole? Role => null;
    }

    private sealed class NoScope : ITenantWriteScope
    {
        public Guid? HouseholdId => null;

        public IDisposable ForHousehold(Guid householdId) => throw new NotSupportedException();
    }
}
