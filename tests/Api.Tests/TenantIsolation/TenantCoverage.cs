using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Foodplanner.Api.Tests.TenantIsolation;

/// <summary>
/// Compares the entity types of an EF model with the isolation registrations. Pure, so the tests can run it on the
/// production model (must be clean) and on a model with an unregistered entity (must fail, naming it).
/// </summary>
public static class TenantCoverage
{
    /// <summary>
    /// Entity types that are in the model on purpose and are not <see cref="ITenantEntity"/>. Adding to this list is a
    /// decision: say why the type needs no tenant filter. Owned types and the implicit join entities of a
    /// many-to-many are never listed; the cross-household link check covers the latter (<see cref="CrossHouseholdLinks"/>).
    /// </summary>
    public static readonly IReadOnlyDictionary<Type, string> Exempt = new Dictionary<Type, string>
    {
        [typeof(Household)] = "The tenant root. It has no HouseholdId; households are reached through Membership (Q-006).",
        [typeof(Unit)] = "Global measuring units (T-020): the seed defines them, no household owns or creates one, no HouseholdId. Read-only at request time because nothing writes the set.",
        [typeof(AppUser)] = "Global login identity (ADR 0004, T-009), not household data.",
        [typeof(IdentityUserClaim<Guid>)] = "Global login identity (T-009).",
        [typeof(IdentityUserLogin<Guid>)] = "Global login identity (T-009).",
        [typeof(IdentityUserToken<Guid>)] = "Global login identity (T-009).",
    };

    public static List<string> Problems(
        IModel model, IEnumerable<Type> registered, IReadOnlyDictionary<Type, string>? exempt = null, IEnumerable<Type>? registeredAsCatalog = null)
    {
        exempt ??= Exempt;
        var problems = new List<string>();
        var registeredTypes = registered.ToHashSet();
        var entityTypes = model.GetEntityTypes().ToList();
        var tenantTypes = entityTypes.Where(e => typeof(ITenantEntity).IsAssignableFrom(e.ClrType) || typeof(ICatalogEntity).IsAssignableFrom(e.ClrType)).Select(e => e.ClrType).ToHashSet();

        foreach (var type in tenantTypes.Where(t => !registeredTypes.Contains(t)).OrderBy(t => t.Name))
        {
            problems.Add($"Tenant entity '{type.Name}' has no isolation test. Register it in TenantIsolationRegistry.Cases "
                + "(one TenantIsolationCase.For<...> line) so household A is proven unable to read or write its household B rows.");
        }

        // A catalog entity needs the catalog variant (global-row checks); a tenant entity must not use it.
        if (registeredAsCatalog is not null)
        {
            var asCatalog = registeredAsCatalog.ToHashSet();
            foreach (var type in tenantTypes.Where(t => registeredTypes.Contains(t)).OrderBy(t => t.Name))
            {
                var isCatalog = typeof(ICatalogEntity).IsAssignableFrom(type);
                if (isCatalog && !asCatalog.Contains(type))
                {
                    problems.Add($"Catalog entity '{type.Name}' is registered with TenantIsolationCase.For; register it with ForCatalog so the global-row checks run.");
                }
                else if (!isCatalog && asCatalog.Contains(type))
                {
                    problems.Add($"Tenant entity '{type.Name}' is registered with ForCatalog; use TenantIsolationCase.For.");
                }
            }
        }

        foreach (var type in registeredTypes.Where(t => !tenantTypes.Contains(t)).OrderBy(t => t.Name))
        {
            problems.Add($"TenantIsolationRegistry registers '{type.Name}', which is not an ITenantEntity in the model. Remove the stale registration.");
        }

        foreach (var entityType in entityTypes.Where(e => !tenantTypes.Contains(e.ClrType) && !e.IsOwned() && !e.HasSharedClrType)
                     .OrderBy(e => e.ClrType.Name))
        {
            if (!exempt.ContainsKey(entityType.ClrType))
            {
                problems.Add($"Entity '{entityType.ClrType.Name}' is not an ITenantEntity and is not on TenantCoverage.Exempt. "
                    + "Household-owned data must implement ITenantEntity; global data must be exempted with a reason.");
            }
        }

        foreach (var (type, _) in exempt.Where(e => tenantTypes.Contains(e.Key) || entityTypes.All(t => t.ClrType != e.Key)))
        {
            problems.Add(tenantTypes.Contains(type)
                ? $"'{type.Name}' is on the exemption list but is an ITenantEntity or ICatalogEntity; remove it from Exempt and register it."
                : $"'{type.Name}' is on the exemption list but is not in the model; remove the stale exemption.");
        }

        return problems;
    }
}
