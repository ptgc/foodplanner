using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Foodplanner.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Save-time check that a household's row only points at its own household's rows or at global rows (Q-012 option 3,
/// T-073). It is the backstop behind the handlers, which keep resolving referenced ids through the filtered sets and
/// answering 400/404 first. Derived from the EF model, so a new entity or foreign key is covered without any code:
/// for every Added or Modified <see cref="ITenantEntity"/> / <see cref="ICatalogEntity"/> and every foreign key whose
/// principal is also one of those, a new or changed key value must name a row that is global (catalog principals
/// only) or owned by the saving row's household, else <see cref="TenancyViolationException"/> before any SQL is sent.
/// <para>
/// Cost: at most ONE read per distinct principal type per SaveChanges (<c>WHERE key IN (...)</c>, two columns,
/// untracked), and none when no foreign key to a tenant or catalog entity is new or changed. Principals added in the
/// same save are not read: their own Add check already puts them in the saving household. Principals that do not exist
/// are left to the database foreign key. Only single-column Guid foreign keys are supported; any other shape to a
/// tenant or catalog principal throws <see cref="InvalidOperationException"/> so it cannot pass unchecked.
/// </para>
/// </summary>
internal static class TenancyReferenceCheck
{
    internal sealed record Reference(string Entity, string ForeignKey, IEntityType Principal, string KeyName, Guid Household, Guid Value);

    public static List<Reference> Collect(IReadOnlyList<(EntityEntry Entry, Guid Household)> savingRows, IReadOnlyList<EntityEntry> allEntries)
    {
        var references = new List<Reference>();
        HashSet<(IEntityType, Guid)>? added = null;

        foreach (var (entry, household) in savingRows)
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            foreach (var fk in entry.Metadata.GetForeignKeys())
            {
                var principal = fk.PrincipalEntityType;
                if (!IsScoped(principal.ClrType))
                {
                    continue;
                }

                if (fk.Properties.Count != 1 || fk.PrincipalKey.Properties.Count != 1
                    || fk.PrincipalKey.Properties[0].ClrType != typeof(Guid))
                {
                    throw new InvalidOperationException(
                        $"{entry.Metadata.ClrType.Name} -> {principal.ClrType.Name}: the save-time reference check supports only single-column Guid foreign keys to a tenant or catalog entity.");
                }

                var property = entry.Property(fk.Properties[0].Name);
                if (entry.State == EntityState.Modified && !property.IsModified)
                {
                    continue;
                }

                if ((Guid?)property.CurrentValue is not { } value)
                {
                    continue;
                }

                added ??= AddedKeys(allEntries);
                if (added.Contains((principal.GetRootType(), value)))
                {
                    continue;
                }

                references.Add(new(
                    entry.Metadata.ClrType.Name,
                    string.Join("+", fk.Properties.Select(p => p.Name)),
                    principal,
                    fk.PrincipalKey.Properties[0].Name,
                    household,
                    value));
            }
        }

        return references;
    }

    /// <summary>The distinct principal types and the ids to read from each: one query each.</summary>
    public static List<(IEntityType Principal, string KeyName, List<Guid> Ids)> Batches(IEnumerable<Reference> references) =>
        references.GroupBy(r => r.Principal)
            .Select(g => (g.Key, g.First().KeyName, g.Select(r => r.Value).Distinct().ToList()))
            .ToList();

    public static void Verify(IEnumerable<Reference> references, IReadOnlyDictionary<IEntityType, List<ReferencedRowOwner>> owners)
    {
        foreach (var reference in references)
        {
            var found = owners[reference.Principal].FirstOrDefault(o => o.Key == reference.Value);

            // Missing: the database foreign key reports it. Global (null owner): anyone may reference it.
            if (found is { HouseholdId: { } owner } && owner != reference.Household)
            {
                throw new TenancyViolationException(
                    $"{reference.Entity}.{reference.ForeignKey} references a {reference.Principal.ClrType.Name} of another household.");
            }
        }
    }

    private static HashSet<(IEntityType, Guid)> AddedKeys(IReadOnlyList<EntityEntry> entries)
    {
        var keys = new HashSet<(IEntityType, Guid)>();
        foreach (var entry in entries.Where(e => e.State == EntityState.Added))
        {
            if (entry.Metadata.FindPrimaryKey() is { Properties: [var key] } && entry.Property(key.Name).CurrentValue is Guid id)
            {
                keys.Add((entry.Metadata.GetRootType(), id));
            }
        }

        return keys;
    }

    private static bool IsScoped(Type clrType) =>
        typeof(ITenantEntity).IsAssignableFrom(clrType) || typeof(ICatalogEntity).IsAssignableFrom(clrType);
}
