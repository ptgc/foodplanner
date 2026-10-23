using System.Linq.Expressions;
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

            var builder = modelBuilder.Entity(clrType);

            if (typeof(ITenantEntity).IsAssignableFrom(clrType))
            {
                builder.HasQueryFilter(AppDbContext.TenantFilterName, TenantFilter(clrType, context));

                // UPDATE and DELETE then also match the original HouseholdId in SQL, so a detached entity that
                // carries this household's id but another household's row id affects no row (concurrency exception).
                builder.Property(nameof(ITenantEntity.HouseholdId)).IsConcurrencyToken();
            }

            if (typeof(Entity).IsAssignableFrom(clrType))
            {
                builder.Property<DateTimeOffset>(CreatedAt);
                builder.Property<DateTimeOffset>(UpdatedAt);
            }
        }
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
