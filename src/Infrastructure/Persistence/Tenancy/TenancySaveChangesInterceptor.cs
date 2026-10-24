using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Foodplanner.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Write guard, reference check and audit timestamps. Runs before SaveChanges sends any SQL. Every tracked
/// <see cref="ITenantEntity"/> is checked first, then its new or changed foreign keys to other tenant or catalog rows
/// (<see cref="TenancyReferenceCheck"/>: at most one read per principal type); only when all pass are household ids and
/// timestamps assigned.
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
        if (eventData.Context is AppDbContext context)
        {
            var plan = Prepare(context);
            var owners = new Dictionary<IEntityType, List<ReferencedRowOwner>>();
            foreach (var (principal, keyName, ids) in TenancyReferenceCheck.Batches(plan.References))
            {
                owners[principal] = ReferenceOwnerLookup.Find(context, principal.ClrType, keyName, ids);
            }

            TenancyReferenceCheck.Verify(plan.References, owners);
            Complete(context, plan);
        }

        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is AppDbContext context)
        {
            var plan = Prepare(context);
            var owners = new Dictionary<IEntityType, List<ReferencedRowOwner>>();
            foreach (var (principal, keyName, ids) in TenancyReferenceCheck.Batches(plan.References))
            {
                owners[principal] = await ReferenceOwnerLookup.FindAsync(context, principal.ClrType, keyName, ids, cancellationToken);
            }

            TenancyReferenceCheck.Verify(plan.References, owners);
            Complete(context, plan);
        }

        return result;
    }

    private sealed record SavePlan(
        List<EntityEntry> Entries,
        List<(PropertyEntry Property, Guid HouseholdId)> Assignments,
        List<TenancyReferenceCheck.Reference> References);

    /// <summary>
    /// Runs the write guard on every tracked tenant or catalog row and collects the foreign keys to check. Changes
    /// nothing: ids and timestamps are assigned by <see cref="Complete"/> only after every check, including the
    /// reference check (T-073), has passed.
    /// </summary>
    private static SavePlan Prepare(AppDbContext context)
    {
        // The interceptor runs before SaveChanges' own DetectChanges; detect here so the guard sees every change.
        context.ChangeTracker.DetectChanges();
        var entries = context.ChangeTracker.Entries().ToList();

        var assignments = new List<(PropertyEntry Property, Guid HouseholdId)>();
        var savingRows = new List<(EntityEntry Entry, Guid Household)>();
        foreach (var entry in entries.Where(e => e.Entity is ITenantEntity or ICatalogEntity))
        {
            var assign = Check(entry, context);
            var property = entry.Property(nameof(ITenantEntity.HouseholdId));
            if (assign is { } stamped)
            {
                assignments.Add((property, stamped));
            }

            // The household the row has once saved: the stamped one, the one it carries, or the stored one.
            var effective = assign ?? (Guid?)(entry.State == EntityState.Added ? property.CurrentValue : property.OriginalValue);
            if (entry.State is EntityState.Added or EntityState.Modified && effective is { } owner)
            {
                savingRows.Add((entry, owner));
            }
        }

        return new(entries, assignments, TenancyReferenceCheck.Collect(savingRows, entries));
    }

    private static void Complete(AppDbContext context, SavePlan plan)
    {
        var entries = plan.Entries;
        foreach (var (property, householdId) in plan.Assignments)
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
