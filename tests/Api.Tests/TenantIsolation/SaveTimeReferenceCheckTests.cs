using System.Data.Common;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Recipes;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Foodplanner.Api.Tests.TenantIsolation;

/// <summary>
/// The foreign keys the save-time reference check (T-073) must guard, derived from the production model: every
/// foreign key of a tenant or catalog entity whose principal is also a tenant or catalog entity. A new entity or
/// foreign key shows up here without any edit.
/// </summary>
public static class ReferenceForeignKeys
{
    public sealed record Key(Type Dependent, string Property, Type Principal)
    {
        public override string ToString() => $"{Dependent.Name}.{Property} -> {Principal.Name}";
    }

    public static List<Key> All(IModel model) =>
        [.. model.GetEntityTypes()
            .Where(e => IsScoped(e.ClrType))
            .SelectMany(e => e.GetDeclaredForeignKeys()
                .Where(fk => IsScoped(fk.PrincipalEntityType.ClrType))
                .Select(fk => new Key(e.ClrType, string.Join("+", fk.Properties.Select(p => p.Name)), fk.PrincipalEntityType.ClrType)))
            .OrderBy(k => k.ToString(), StringComparer.Ordinal)];

    public static bool IsScoped(Type type) =>
        typeof(ITenantEntity).IsAssignableFrom(type) || typeof(ICatalogEntity).IsAssignableFrom(type);
}

/// <summary>
/// Q-012 option 3: a save that adds or changes a foreign key to another household's row throws
/// <see cref="TenancyViolationException"/>; references to the household's own rows and to global rows save. One theory
/// row per foreign key of the production model, so a failure names it. Handlers still answer their own 400/404 first.
/// </summary>
public sealed class SaveTimeReferenceCheckTests(TenancyDatabase db) : IClassFixture<TenancyDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly List<ReferenceForeignKeys.Key> ForeignKeys = ReferenceForeignKeys.All(TenantIsolationEnvironment.ProductionModel());

    public static TheoryData<string> Keys => [.. ForeignKeys.Select(k => k.ToString())];

    public static TheoryData<string> KeysToCatalogPrincipals =>
        [.. ForeignKeys.Where(k => typeof(ICatalogEntity).IsAssignableFrom(k.Principal)).Select(k => k.ToString())];

    private TenantIsolationEnvironment Env => new(db.ConnectionString);

    private static ReferenceForeignKeys.Key Find(string name) => ForeignKeys.Single(k => k.ToString() == name);

    private static TenantIsolationCase CaseFor(Type type) =>
        TenantIsolationRegistry.Cases.SingleOrDefault(c => c.EntityType == type)
        ?? throw new Xunit.Sdk.XunitException(
            $"{type.Name} has no TenantIsolationRegistry case, so the reference check cannot be exercised for it. Register it there.");

    [Fact]
    public void The_model_yields_the_expected_foreign_keys_and_every_one_has_test_data()
    {
        var names = ForeignKeys.Select(k => k.ToString()).ToList();

        Assert.Contains("RecipeTag.TagId -> Tag", names); // join row to a catalog principal
        Assert.Contains("RecipeIngredient.IngredientId -> Ingredient", names);
        Assert.Contains("MealPlanEntry.RecipeId -> Recipe", names);
        Assert.Contains("ShoppingListItemSource.RecipeId -> Recipe", names);
        Assert.All(ForeignKeys, k =>
        {
            _ = CaseFor(k.Dependent); // fails loudly, naming the type, when a new entity has no case
            _ = CaseFor(k.Principal);
        });
    }

    private static Task SetForeignKeyAsync(AppDbContext context, object row, ReferenceForeignKeys.Key key, Guid value)
    {
        context.Entry(row).Property(key.Property).CurrentValue = value;
        return Task.CompletedTask;
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task Adding_a_row_that_references_another_households_row_throws(string name)
    {
        var key = Find(name);
        var env = Env;
        var (a, b) = (await env.NewHouseholdAsync(), await env.NewHouseholdAsync());
        var row = await CaseFor(key.Dependent).NewUnsavedRowAsync(env, a);
        var foreign = await CaseFor(key.Principal).SeedRowAsync(env, b);

        await using var context = env.ContextFor(a);
        context.Add(row);
        await SetForeignKeyAsync(context, row, key, foreign);
        var ex = await Assert.ThrowsAsync<TenancyViolationException>(() => context.SaveChangesAsync(Ct));

        Assert.Contains(key.Dependent.Name, ex.Message, StringComparison.Ordinal);
        Assert.Contains(key.Property, ex.Message, StringComparison.Ordinal);
        Assert.Contains(key.Principal.Name, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(foreign.ToString(), ex.Message, StringComparison.OrdinalIgnoreCase); // no ids in the message
        await using var check = env.ContextFor(a);
        Assert.Null(await check.FindAsync(key.Dependent, [((Foodplanner.Domain.Common.Entity)row).Id], Ct));
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task Changing_a_foreign_key_of_a_saved_row_to_another_households_row_throws_and_the_row_keeps_its_value(string name)
    {
        var key = Find(name);
        var env = Env;
        var (a, b) = (await env.NewHouseholdAsync(), await env.NewHouseholdAsync());
        var id = await CaseFor(key.Dependent).SeedRowAsync(env, a);
        var foreign = await CaseFor(key.Principal).SeedRowAsync(env, b);
        var before = await env.SnapshotAsync(key.Dependent, id);

        await using (var context = env.ContextFor(a))
        {
            var row = await context.FindAsync(key.Dependent, [id], Ct);
            Assert.NotNull(row);
            await SetForeignKeyAsync(context, row, key, foreign);
            await Assert.ThrowsAsync<TenancyViolationException>(() => context.SaveChangesAsync(Ct));
        }

        Assert.Equal(before, await env.SnapshotAsync(key.Dependent, id));
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task Referencing_the_own_households_row_saves_added_and_modified(string name)
    {
        var key = Find(name);
        var env = Env;
        var a = await env.NewHouseholdAsync();
        var principal = CaseFor(key.Principal);

        var row = await CaseFor(key.Dependent).NewUnsavedRowAsync(env, a);
        var own = await principal.SeedRowAsync(env, a);
        await using (var context = env.ContextFor(a))
        {
            context.Add(row);
            await SetForeignKeyAsync(context, row, key, own);
            await context.SaveChangesAsync(Ct);
        }

        var other = await principal.SeedRowAsync(env, a);
        await using (var context = env.ContextFor(a))
        {
            var saved = await context.FindAsync(key.Dependent, [((Foodplanner.Domain.Common.Entity)row).Id], Ct);
            await SetForeignKeyAsync(context, saved!, key, other);
            await context.SaveChangesAsync(Ct);
        }
    }

    [Theory]
    [MemberData(nameof(KeysToCatalogPrincipals))]
    public async Task Referencing_a_global_row_saves_added_and_modified(string name)
    {
        var key = Find(name);
        var env = Env;
        var a = await env.NewHouseholdAsync();
        var principal = CaseFor(key.Principal);
        var global1 = await principal.SeedGlobalRowAsync(env);
        var global2 = await principal.SeedGlobalRowAsync(env);

        var row = await CaseFor(key.Dependent).NewUnsavedRowAsync(env, a);
        await using (var context = env.ContextFor(a))
        {
            context.Add(row);
            await SetForeignKeyAsync(context, row, key, global1);
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = env.ContextFor(a))
        {
            var saved = await context.FindAsync(key.Dependent, [((Foodplanner.Domain.Common.Entity)row).Id], Ct);
            await SetForeignKeyAsync(context, saved!, key, global2);
            await context.SaveChangesAsync(Ct);
        }
    }

    [Fact]
    public async Task The_synchronous_SaveChanges_is_guarded_too()
    {
        var env = Env;
        var (a, b) = (await env.NewHouseholdAsync(), await env.NewHouseholdAsync());
        var recipeOfB = new Recipe("Other " + Guid.NewGuid().ToString("N"), null, 2, null, null);
        await env.SaveAsync(b, recipeOfB);

        using var context = env.ContextFor(a);
        context.Add(new RecipeStep(recipeOfB.Id, 0, "Stir"));

        Assert.Throws<TenancyViolationException>(() => context.SaveChanges());
    }

    [Fact]
    public async Task A_reference_to_a_row_that_does_not_exist_is_left_to_the_database_foreign_key()
    {
        var env = Env;
        var a = await env.NewHouseholdAsync();

        await using var context = env.ContextFor(a);
        context.Add(new RecipeStep(Guid.NewGuid(), 0, "Stir"));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_parent_and_children_added_in_one_save_pass_without_reading_the_added_parent()
    {
        var env = Env;
        var a = await env.NewHouseholdAsync();
        var counter = new CommandCounter();

        await using var context = ContextWith(a, counter);
        var recipe = new Recipe("New " + Guid.NewGuid().ToString("N"), null, 2, null, null);
        context.Add(recipe);
        context.Add(new RecipeStep(recipe.Id, 0, "Stir"));
        context.Add(new RecipeIngredient(recipe.Id, 0, IngredientSeed.All[0].Id, 1m, UnitSeed.All[0].Id, null));
        await context.SaveChangesAsync(Ct);

        // Only the ingredient (the catalog principal) is read; the recipe is in the same save.
        Assert.Equal(1, counter.Selects);
    }

    // Query count (T-073): at most one read per distinct principal type per SaveChanges, however many rows and ids.
    [Fact]
    public async Task The_number_of_reads_per_save_is_one_per_principal_type_and_zero_when_no_reference_changes()
    {
        var env = Env;
        var a = await env.NewHouseholdAsync();
        var recipe = new Recipe("Counted " + Guid.NewGuid().ToString("N"), null, 2, null, null);
        await env.SaveAsync(a, recipe);
        var counter = new CommandCounter();

        await using (var context = ContextWith(a, counter))
        {
            // 50 lines, 50 different ingredients, one recipe: two principal types (Recipe, Ingredient); Unit is global, not scoped.
            for (var i = 0; i < 50; i++)
            {
                context.Add(new RecipeIngredient(recipe.Id, i, IngredientSeed.All[i].Id, 1m, UnitSeed.All[0].Id, null));
            }

            await context.SaveChangesAsync(Ct);
        }

        Assert.Equal(2, counter.Selects);
        Assert.True(counter.Inserts >= 1);

        counter.Reset();
        await using (var context = ContextWith(a, counter))
        {
            var line = await context.RecipeIngredients.FirstAsync(Ct);
            counter.Reset(); // the load above is the test's own read
            context.Entry(line).Property(nameof(RecipeIngredient.Note)).CurrentValue = "finely chopped";
            await context.SaveChangesAsync(Ct);
        }

        Assert.Equal(0, counter.Selects); // a change that touches no reference reads nothing

        counter.Reset();
        await using (var context = ContextWith(a, counter))
        {
            var line = await context.RecipeIngredients.FirstAsync(Ct);
            var other = IngredientSeed.All[60].Id;
            counter.Reset();
            context.Entry(line).Property(nameof(RecipeIngredient.IngredientId)).CurrentValue = other;
            await context.SaveChangesAsync(Ct);
        }

        Assert.Equal(1, counter.Selects); // one changed reference to one principal type: one read
    }

    private AppDbContext ContextWith(Guid household, CommandCounter counter) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(db.ConnectionString).AddInterceptors(counter).Options,
            new FixedHousehold(household),
            new TenantWriteScope(),
            TimeProvider.System);
}

/// <summary>Invariants behind the save-time reference check that the per-key theories do not pin (T-073 review).</summary>
public sealed class SaveTimeReferenceCheckInvariantTests(TenancyDatabase db) : IClassFixture<TenancyDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TenantIsolationEnvironment Env => new(db.ConnectionString);

    [Fact]
    public async Task A_principal_of_another_household_and_a_dependent_referencing_it_in_the_same_save_is_refused()
    {
        var env = Env;
        var (a, b) = (await env.NewHouseholdAsync(), await env.NewHouseholdAsync());

        await using var context = env.ContextFor(a);
        var recipe = new Recipe("Smuggled " + Guid.NewGuid().ToString("N"), null, 2, null, null);
        context.Add(recipe);
        context.Entry(recipe).Property(nameof(ITenantEntity.HouseholdId)).CurrentValue = b;
        context.Add(new RecipeStep(recipe.Id, 0, "Stir"));

        // The same-save shortcut is safe only because the principal's own Add check refuses another household.
        await Assert.ThrowsAsync<TenancyViolationException>(() => context.SaveChangesAsync(Ct));
        await using var check = env.ContextFor(b);
        Assert.False(await check.Recipes.AnyAsync(r => r.Id == recipe.Id, Ct));
    }

    [Fact]
    public async Task A_refused_save_changes_nothing_and_a_following_valid_save_works()
    {
        var env = Env;
        var (a, b) = (await env.NewHouseholdAsync(), await env.NewHouseholdAsync());
        var recipeOfB = new Recipe("Other " + Guid.NewGuid().ToString("N"), null, 2, null, null);
        await env.SaveAsync(b, recipeOfB);
        var own = new Recipe("Own " + Guid.NewGuid().ToString("N"), null, 2, null, null);
        await env.SaveAsync(a, own);

        await using var context = env.ContextFor(a);
        var bad = new RecipeStep(recipeOfB.Id, 0, "Stir");
        context.Add(bad);
        await Assert.ThrowsAsync<TenancyViolationException>(() => context.SaveChangesAsync(Ct));

        var entry = context.Entry(bad);
        Assert.Equal(EntityState.Added, entry.State);
        Assert.Equal(Guid.Empty, entry.Property(nameof(ITenantEntity.HouseholdId)).CurrentValue); // not stamped
        Assert.Equal(default(DateTimeOffset), entry.Property("CreatedAt").CurrentValue); // no timestamps
        Assert.Equal(default(DateTimeOffset), entry.Property("UpdatedAt").CurrentValue);

        entry.State = EntityState.Detached;
        context.Add(new RecipeStep(own.Id, 0, "Stir"));
        await context.SaveChangesAsync(Ct);
    }
}

/// <summary>Counts the commands a context sends: reads (text starts with SELECT) and the save's writes.</summary>
internal sealed class CommandCounter : DbCommandInterceptor
{
    public int Selects { get; private set; }

    public int Inserts { get; private set; }

    public void Reset() => (Selects, Inserts) = (0, 0);

    private void Count(DbCommand command)
    {
        var text = command.CommandText.TrimStart();
        if (text.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            Selects++;
        }
        else if (text.Contains("INSERT", StringComparison.OrdinalIgnoreCase))
        {
            Inserts++;
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Count(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Count(command);
        return ValueTask.FromResult(result);
    }
}
