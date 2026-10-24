using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Api.Tests.TenantIsolation;

/// <summary>
/// The tenant-isolation suite's registrations (CLAUDE.md rule 1). Every <see cref="ITenantEntity"/> (<c>For</c>) and every
/// <see cref="Foodplanner.Domain.Catalog.ICatalogEntity"/> (<c>ForCatalog</c>, which adds the global-row checks) in the
/// production model needs a <see cref="TenantIsolationCase"/> here, or <c>TenantIsolationCoverageTests</c> fails the build and
/// names the entity.
/// <para>
/// To add an entity: 1) add one <c>TenantIsolationCase.For</c> line to <see cref="Cases"/>, whose factory builds a
/// new unsaved row for the given household (seeding any parent rows through the environment) and whose mutate
/// changes one persisted column; 2) run the tests. Nothing else: the harness proves read, insert, update, delete,
/// HouseholdId change and ExecuteUpdate/ExecuteDelete isolation for it.
/// </para>
/// <para>
/// The factory may ignore its household argument: the harness stamps HouseholdId on the new row itself, so
/// entities whose household is assigned by Infrastructure on Add (the normal pattern, no constructor parameter, no
/// public setter) register exactly like Membership and Invite. Limits: single Guid key <c>Entity.Id</c>; the factory
/// adds exactly one row of the type per household. Seed parent rows with <c>env.SaveAsync(household, parent)</c>.
/// </para>
/// <para>
/// Types that are deliberately not tenant entities (the tenant root and the global identity tables) are on
/// <see cref="TenantCoverage.Exempt"/>, each with a reason.
/// </para>
/// </summary>
public static class TenantIsolationRegistry
{
    public static IReadOnlyList<TenantIsolationCase> Cases { get; } =
    [
        TenantIsolationCase.For<Membership>(
            async (env, household) => new Membership(household, await env.NewUserAsync(), HouseholdRole.Adult),
            entry => entry.Property(m => m.Role).CurrentValue = HouseholdRole.Child),

        TenantIsolationCase.For<Invite>(
            async (env, household) => new Invite(
                household, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), HouseholdRole.Child, DateTimeOffset.UtcNow.AddDays(3), await env.NewUserAsync()),
            entry => entry.Property(i => i.ExpiresAt).CurrentValue = DateTimeOffset.UtcNow.AddDays(30)),

        // Global-or-household catalog entity (T-020): also proven against global rows (visible to all, read-only).
        TenantIsolationCase.ForCatalog<Ingredient>(
            (_, household) => Task.FromResult(new Ingredient(household, "Isolation " + Guid.NewGuid().ToString("N"), isStaple: false, UnitDimension.Mass)),
            entry => entry.Property(i => i.IsStaple).CurrentValue = true),
    ];

    /// <summary>The registered catalog entities, for the global-row checks.</summary>
    public static IEnumerable<string> CatalogNames => Cases.Where(c => c.IsCatalog).Select(c => c.Name);

    public static TenantIsolationCase Find(string name) =>
        Cases.Single(c => c.Name == name);

    public static IEnumerable<string> Names => Cases.Select(c => c.Name);
}
