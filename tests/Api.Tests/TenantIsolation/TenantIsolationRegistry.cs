using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.MealPlans;
using Foodplanner.Domain.Recipes;
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

        // Global-or-household categories and tags (T-023), like Ingredient.
        TenantIsolationCase.ForCatalog<Tag>(
            (_, household) => Task.FromResult(new Tag(household, "Isolation " + Guid.NewGuid().ToString("N"), TagKind.Tag)),
            entry => entry.Property(t => t.Kind).CurrentValue = TagKind.Category),

        // Recipes (T-021). Children point to their recipe by id only; the parent row is seeded through the environment.
        TenantIsolationCase.For<Recipe>(
            (_, _) => Task.FromResult(new Recipe("Isolation " + Guid.NewGuid().ToString("N"), null, 4, 10, 20)),
            entry => entry.Property(r => r.Title).CurrentValue = "Changed"),

        TenantIsolationCase.For<RecipeStep>(
            async (env, household) =>
            {
                var recipe = new Recipe("Parent " + Guid.NewGuid().ToString("N"), null, 2, null, null);
                await env.SaveAsync(household, recipe);
                return new RecipeStep(recipe.Id, 0, "Stir");
            },
            entry => entry.Property(s => s.Text).CurrentValue = "Stir again"),

        // The ingredient and unit are seeded global rows, which every household may reference.
        TenantIsolationCase.For<RecipeIngredient>(
            async (env, household) =>
            {
                var recipe = new Recipe("Parent " + Guid.NewGuid().ToString("N"), null, 2, null, null);
                await env.SaveAsync(household, recipe);
                return new RecipeIngredient(recipe.Id, 0, IngredientSeed.All[0].Id, 1.5m, UnitSeed.All[0].Id, null);
            },
            entry => entry.Property(i => i.Note).CurrentValue = "finely chopped"),

        // The join row (T-023): the tag is a seeded global row, which every household may reference. Mutate swaps it for another global tag.
        TenantIsolationCase.For<RecipeTag>(
            async (env, household) =>
            {
                var recipe = new Recipe("Parent " + Guid.NewGuid().ToString("N"), null, 2, null, null);
                await env.SaveAsync(household, recipe);
                return new RecipeTag(recipe.Id, TagSeed.All[5].Id);
            },
            entry => entry.Property(l => l.TagId).CurrentValue = TagSeed.All[6].Id),

        // Meal plans (T-030). A plan is one row per week; an entry points to its plan, a recipe of the same household and the user who picked it.
        TenantIsolationCase.For<MealPlan>(
            (_, _) => Task.FromResult(new MealPlan(RandomMonday())),
            entry => entry.Property(p => p.WeekStart).CurrentValue = new DateOnly(2026, 11, 2)),

        TenantIsolationCase.For<MealPlanEntry>(
            async (env, household) =>
            {
                var recipe = new Recipe("Parent " + Guid.NewGuid().ToString("N"), null, 2, null, null);
                await env.SaveAsync(household, recipe);
                // A random week: the factory may run more than once for a household, and a household has one plan per week.
                var plan = new MealPlan(RandomMonday());
                await env.SaveAsync(household, plan);
                return new MealPlanEntry(plan.Id, plan.WeekStart, plan.WeekStart.AddDays(1), recipe.Id, 4, await env.NewUserAsync());
            },
            entry => entry.Property(e => e.Servings).CurrentValue = 6),
    ];

    private static DateOnly RandomMonday() => new DateOnly(2026, 10, 26).AddDays(7 * Random.Shared.Next(2, 100_000));

    /// <summary>The registered catalog entities, for the global-row checks.</summary>
    public static IEnumerable<string> CatalogNames => Cases.Where(c => c.IsCatalog).Select(c => c.Name);

    public static TenantIsolationCase Find(string name) =>
        Cases.Single(c => c.Name == name);

    public static IEnumerable<string> Names => Cases.Select(c => c.Name);
}
