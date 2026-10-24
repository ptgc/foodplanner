using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Foodplanner.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Write guard and audit timestamps. Runs before SaveChanges sends any SQL. Every tracked
/// <see cref="ITenantEntity"/> is checked first; only when all pass are household ids and timestamps assigned.
/// Stateless: it reads the household, write scope and clock from the <see cref="AppDbContext"/> being saved.
/// </summary>
internal sealed class TenancySaveChangesInterceptor : SaveChangesInterceptor
{
    public static readonly TenancySaveChangesInterceptor Instance = new();

    private TenancySaveChangesInterceptor()
    {
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Apply(DbContext? dbContext)
    {
        if (dbContext is not AppDbContext context)
        {
            return;
        }

        // The interceptor runs before SaveChanges' own DetectChanges; detect here so the guard sees every change.
        context.ChangeTracker.DetectChanges();
        var entries = context.ChangeTracker.Entries().ToList();

        var assignments = new List<(PropertyEntry Property, Guid HouseholdId)>();
        foreach (var entry in entries)
        {
            if (entry.Entity is ITenantEntity or ICatalogEntity && Check(entry, context) is { } assign)
            {
                assignments.Add((entry.Property(nameof(ITenantEntity.HouseholdId)), assign));
            }
        }

        foreach (var (property, householdId) in assignments)
        {
            property.CurrentValue = householdId;
        }

        var now = context.TimeProvider.GetUtcNow();
        foreach (var entry in entries.Where(e => e.Entity is Entity))
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Property(TenancyModelConventions.CreatedAt).CurrentValue = now;
                    entry.Property(TenancyModelConventions.UpdatedAt).CurrentValue = now;
                    break;
                case EntityState.Modified:
                    var createdAt = entry.Property(TenancyModelConventions.CreatedAt);
                    createdAt.CurrentValue = createdAt.OriginalValue;
                    createdAt.IsModified = false;
                    entry.Property(TenancyModelConventions.UpdatedAt).CurrentValue = now;
                    break;
            }
        }
    }

    /// <summary>
    /// Throws on a violation. Returns the household id to assign to an Added entity, if any. A catalog entity
    /// (<see cref="ICatalogEntity"/>) follows the tenant rules and two more: a row can never be added without a household,
    /// so request code can never create a global row, and a global row (null household) can never be updated or
    /// deleted. Global rows come from the seed (migration data), which does not pass through SaveChanges.
    /// </summary>
    private static Guid? Check(EntityEntry entry, AppDbContext context)
    {
        var property = entry.Property(nameof(ITenantEntity.HouseholdId));
        var isCatalog = entry.Entity is ICatalogEntity;
        var entityName = entry.Metadata.ClrType.Name;
        var current = context.CurrentHouseholdId;
        var forWrite = context.WriteScope.HouseholdId ?? current;

        switch (entry.State)
        {
            case EntityState.Added:
                {
                    if (forWrite is not { } household)
                    {
                        throw new TenancyViolationException($"Cannot add {entityName} without a household context.");
                    }

                    var value = (Guid?)property.CurrentValue;
                    if (value is null)
                    {
                        throw new TenancyViolationException($"Cannot add {entityName} without a household: global rows are written only by the seed.");
                    }

                    if (value == Guid.Empty)
                    {
                        return household;
                    }

                    if (value != household)
                    {
                        throw new TenancyViolationException($"Cannot add {entityName} for another household.");
                    }

                    return null;
                }

            case EntityState.Modified:
                EnsureHouseholdUnchanged(property, entityName);
                EnsureOwnedBy(property, forWrite, entityName, "update", isCatalog);
                return null;

            case EntityState.Deleted:
                EnsureHouseholdUnchanged(property, entityName);
                EnsureOwnedBy(property, current, entityName, "delete", isCatalog);
                return null;

            default:
                return null;
        }
    }

    private static void EnsureHouseholdUnchanged(PropertyEntry property, string entityName)
    {
        if (!Equals(property.OriginalValue, property.CurrentValue))
        {
            throw new TenancyViolationException($"The HouseholdId of {entityName} cannot be changed.");
        }

        // DbSet.Update on a detached entity flags every property as modified without changing it. Never write the column.
        property.IsModified = false;
    }

    private static void EnsureOwnedBy(PropertyEntry property, Guid? household, string entityName, string action, bool isCatalog)
    {
        if (isCatalog && property.OriginalValue is null)
        {
            throw new TenancyViolationException($"Cannot {action} {entityName}: it is a global catalog row, read-only to households.");
        }

        if (household is null)
        {
            throw new TenancyViolationException($"Cannot {action} {entityName} without a household context.");
        }

        if ((Guid)property.OriginalValue! != household.Value)
        {
            throw new TenancyViolationException($"Cannot {action} {entityName} of another household.");
        }
    }
}
