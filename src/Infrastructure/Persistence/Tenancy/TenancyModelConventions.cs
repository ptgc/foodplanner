using System.Linq.Expressions;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Model conventions applied by <see cref="AppDbContext"/> after all entities are configured. Feature code never
/// adds the tenant filter or the audit properties by hand.
/// </summary>
internal static class TenancyModelConventions
{
    public const string CreatedAt = "CreatedAt";
    public const string UpdatedAt = "UpdatedAt";

    public static void Apply(ModelBuilder modelBuilder, AppDbContext context)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            var clrType = entityType.ClrType;
            if (entityType.IsOwned() || entityType.BaseType is not null)
            {
                // Filters and audit columns live on the root of a hierarchy; derived types inherit them.
                if (entityType.BaseType is not null && typeof(ITenantEntity).IsAssignableFrom(clrType)
                    && !typeof(ITenantEntity).IsAssignableFrom(entityType.GetRootType().ClrType))
                {
                    throw new InvalidOperationException(
                        $"{clrType.Name} implements ITenantEntity but its root type {entityType.GetRootType().ClrType.Name} does not.");
                }

                continue;
            }

            // Configure through the mutable entity type, never modelBuilder.Entity(clrType): that throws for
            // shared-type entity types such as the implicit Dictionary<string, object> join entity of a many-to-many,
            // and would miss a shared-type entity that implements ITenantEntity.
            if (typeof(ITenantEntity).IsAssignableFrom(clrType))
            {
                entityType.SetQueryFilter(AppDbContext.TenantFilterName, TenantFilter(clrType, context));

                // UPDATE and DELETE then also match the original HouseholdId in SQL, so a detached entity that
                // carries this household's id but another household's row id affects no row (concurrency exception).
                var householdId = entityType.FindProperty(nameof(ITenantEntity.HouseholdId))
                    ?? entityType.AddProperty(nameof(ITenantEntity.HouseholdId));
                householdId.IsConcurrencyToken = true;
            }
            else if (typeof(ICatalogEntity).IsAssignableFrom(clrType))
            {
                // Same filter name as tenant entities (one name, so the bypass rules and the architecture rules cover
                // both), but the expression admits the global rows: HouseholdId is null or the current household.
                entityType.SetQueryFilter(AppDbContext.TenantFilterName, CatalogFilter(clrType, context));
                var householdId = entityType.FindProperty(nameof(ICatalogEntity.HouseholdId))
                    ?? entityType.AddProperty(nameof(ICatalogEntity.HouseholdId), typeof(Guid?));
                householdId.IsConcurrencyToken = true;
            }

            if (typeof(Entity).IsAssignableFrom(clrType))
            {
                _ = entityType.FindProperty(CreatedAt) ?? entityType.AddProperty(CreatedAt, typeof(DateTimeOffset));
                _ = entityType.FindProperty(UpdatedAt) ?? entityType.AddProperty(UpdatedAt, typeof(DateTimeOffset));
            }
        }
    }

    /// <summary>
    /// Builds <c>e =&gt; e.HouseholdId == null || e.HouseholdId == context.CurrentHouseholdId</c> for a catalog entity.
    /// With no household context only the global rows remain.
    /// </summary>
    private static LambdaExpression CatalogFilter(Type clrType, AppDbContext context)
    {
        var entity = Expression.Parameter(clrType, "e");
        var householdId = Expression.Property(entity, nameof(ICatalogEntity.HouseholdId));
        var current = Expression.Property(Expression.Constant(context), nameof(AppDbContext.CurrentHouseholdId));
        var isGlobal = Expression.Equal(householdId, Expression.Constant(null, typeof(Guid?)));
        return Expression.Lambda(Expression.OrElse(isGlobal, Expression.Equal(householdId, current)), entity);
    }

    /// <summary>
    /// Builds <c>e =&gt; e.HouseholdId == context.CurrentHouseholdId</c>. The context is referenced as an instance
    /// member, so EF Core re-reads it from the executing context on every query; the model is cached across
    /// requests and must not capture a household value.
    /// </summary>
    private static LambdaExpression TenantFilter(Type clrType, AppDbContext context)
    {
        var entity = Expression.Parameter(clrType, "e");
        var householdId = Expression.Convert(
            Expression.Property(entity, nameof(ITenantEntity.HouseholdId)), typeof(Guid?));
        var current = Expression.Property(Expression.Constant(context), nameof(AppDbContext.CurrentHouseholdId));
        return Expression.Lambda(Expression.Equal(householdId, current), entity);
    }
}
