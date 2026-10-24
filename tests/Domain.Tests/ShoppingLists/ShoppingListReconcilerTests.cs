using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Common;
using Foodplanner.Domain.Quantities;
using Foodplanner.Domain.ShoppingLists;

namespace Foodplanner.Domain.Tests.ShoppingLists;

public class ShoppingListReconcilerTests
{
    // Units as the database returns them: with their seeded ids (UnitCatalog.Seed units have no id).
    private static readonly UnitCatalog Catalog = new(UnitSeed.All.Select(r =>
    {
        var unit = r.ToUnit();
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(unit, r.Id);
        return unit;
    }));

    private static readonly Guid Rice = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Milk = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Salt = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid R1 = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid R2 = Guid.Parse("10000000-0000-0000-0000-000000000002");

    private static Quantity Q(decimal? amount, string unit) => new(amount, Catalog.Get(unit));

    private static StoredQuantity S(decimal amount, string unit) => new(amount, Catalog.Get(unit).Id);

    private static GeneratedList Generate(params PlannedMeal[] meals) => ShoppingListGenerator.Generate(meals, Catalog);

    private static PlannedMeal Meal(Guid recipe, params PlannedIngredient[] ingredients) => new(recipe, 4, 4, ingredients);

    private static PlannedIngredient Ing(Guid id, string name, Quantity quantity, bool staple = false) => new(id, name, staple, quantity);

    private static ExistingGeneratedItem Stored(
        Guid itemId, Guid ingredient, ShoppingSection section, bool isChecked, StoredQuantity[] quantities, params Guid[] recipes) =>
        new(itemId, ingredient, section, isChecked, quantities, recipes);

    [Fact]
    public void An_empty_list_gets_every_generated_item_and_staple()
    {
        var generated = Generate(Meal(R1, Ing(Rice, "Ris", Q(500, "g")), Ing(Salt, "Salt", Q(null, "stk"), staple: true)));

        var plan = ShoppingListReconciler.Reconcile([], generated);

        Assert.Equal(2, plan.Add.Count);
        var rice = plan.Add.Single(a => a.IngredientId == Rice);
        Assert.Equal(ShoppingSection.Main, rice.Section);
        Assert.Equal([S(500, "g")], rice.Quantities);
        Assert.Equal([R1], rice.RecipeIds);
        var salt = plan.Add.Single(a => a.IngredientId == Salt);
        Assert.Equal(ShoppingSection.CheckYouHave, salt.Section);
        Assert.Empty(salt.Quantities);
        Assert.Empty(plan.Update);
        Assert.Empty(plan.RemoveItemIds);
    }

    [Fact]
    public void To_taste_is_stored_without_an_amount_or_a_unit()
    {
        var generated = Generate(Meal(R1, Ing(Rice, "Ris", Q(null, "stk"))));

        var plan = ShoppingListReconciler.Reconcile([], generated);

        Assert.Equal([new StoredQuantity(null, null)], Assert.Single(plan.Add).Quantities);
    }

    [Fact]
    public void Regenerating_the_same_plan_changes_nothing()
    {
        var generated = Generate(
            Meal(R1, Ing(Rice, "Ris", Q(200, "g")), Ing(Salt, "Salt", Q(1, "g"), staple: true)),
            Meal(R2, Ing(Rice, "Ris", Q(0.3m, "kg"))));
        var first = ShoppingListReconciler.Reconcile([], generated);
        var stored = first.Add.Select(a => Stored(Guid.NewGuid(), a.IngredientId, a.Section, isChecked: true, [.. a.Quantities], [.. a.RecipeIds])).ToList();

        var second = ShoppingListReconciler.Reconcile(stored, generated);

        Assert.True(second.IsEmpty);
    }

    [Fact]
    public void A_checked_item_whose_quantity_changed_stays_and_is_flagged()
    {
        var item = Guid.NewGuid();
        var stored = Stored(item, Rice, ShoppingSection.Main, isChecked: true, [S(500, "g")], R1, R2);

        var plan = ShoppingListReconciler.Reconcile([stored], Generate(Meal(R1, Ing(Rice, "Ris", Q(700, "g"))), Meal(R2, Ing(Rice, "Ris", Q(0, "g")))));

        var update = Assert.Single(plan.Update);
        Assert.Equal(item, update.ItemId);
        Assert.True(update.FlagQuantityChange);
        Assert.Null(update.MoveToSection);
        Assert.Equal([S(700, "g")], update.Quantities);
        Assert.Empty(plan.Add);
        Assert.Empty(plan.RemoveItemIds);
    }

    [Fact]
    public void An_unchecked_item_whose_quantity_changed_is_updated_without_a_flag()
    {
        var stored = Stored(Guid.NewGuid(), Rice, ShoppingSection.Main, isChecked: false, [S(500, "g")], R1);

        var plan = ShoppingListReconciler.Reconcile([stored], Generate(Meal(R1, Ing(Rice, "Ris", Q(700, "g")))));

        var update = Assert.Single(plan.Update);
        Assert.False(update.FlagQuantityChange);
        Assert.Equal([S(700, "g")], update.Quantities);
        Assert.Null(update.RecipeIds);
    }

    [Fact]
    public void A_change_of_unit_is_a_change_of_quantity()
    {
        var stored = Stored(Guid.NewGuid(), Rice, ShoppingSection.Main, isChecked: true, [S(1, "kg")], R1);

        var plan = ShoppingListReconciler.Reconcile([stored], Generate(Meal(R1, Ing(Rice, "Ris", Q(1, "l")))));

        Assert.True(Assert.Single(plan.Update).FlagQuantityChange);
    }

    [Fact]
    public void A_changed_recipe_set_alone_updates_the_sources_and_does_not_flag()
    {
        var stored = Stored(Guid.NewGuid(), Rice, ShoppingSection.Main, isChecked: true, [S(500, "g")], R1);

        var plan = ShoppingListReconciler.Reconcile(
            [stored], Generate(Meal(R1, Ing(Rice, "Ris", Q(300, "g"))), Meal(R2, Ing(Rice, "Ris", Q(200, "g")))));

        var update = Assert.Single(plan.Update);
        Assert.False(update.FlagQuantityChange);
        Assert.Null(update.Quantities);
        Assert.Equal([R1, R2], update.RecipeIds!.Order());
    }

    [Fact]
    public void Items_no_longer_in_the_plan_are_removed_checked_or_not()
    {
        var gone = Stored(Guid.NewGuid(), Milk, ShoppingSection.Main, isChecked: true, [S(500, "ml")], R1);
        var goneStaple = Stored(Guid.NewGuid(), Salt, ShoppingSection.CheckYouHave, isChecked: false, [], R1);
        var kept = Stored(Guid.NewGuid(), Rice, ShoppingSection.Main, isChecked: false, [S(500, "g")], R1);

        var plan = ShoppingListReconciler.Reconcile([gone, goneStaple, kept], Generate(Meal(R1, Ing(Rice, "Ris", Q(500, "g")))));

        Assert.Equal(new[] { gone.ItemId, goneStaple.ItemId }.Order(), plan.RemoveItemIds);
        Assert.Empty(plan.Update);
        Assert.Empty(plan.Add);
    }

    [Fact]
    public void An_ingredient_that_changes_section_is_moved_and_not_flagged()
    {
        var stored = Stored(Guid.NewGuid(), Salt, ShoppingSection.Main, isChecked: true, [S(5, "g")], R1);

        var plan = ShoppingListReconciler.Reconcile([stored], Generate(Meal(R1, Ing(Salt, "Salt", Q(5, "g"), staple: true))));

        var update = Assert.Single(plan.Update);
        Assert.Equal(ShoppingSection.CheckYouHave, update.MoveToSection);
        Assert.False(update.FlagQuantityChange);
        Assert.Empty(update.Quantities!);
    }

    [Fact]
    public void The_result_does_not_depend_on_the_order_of_the_stored_items()
    {
        var a = Stored(Guid.NewGuid(), Rice, ShoppingSection.Main, isChecked: false, [S(100, "g")], R1);
        var b = Stored(Guid.NewGuid(), Milk, ShoppingSection.Main, isChecked: false, [S(100, "ml")], R1);
        var generated = Generate(Meal(R1, Ing(Rice, "Ris", Q(200, "g")), Ing(Milk, "Melk", Q(200, "ml"))));

        var forward = ShoppingListReconciler.Reconcile([a, b], generated);
        var backward = ShoppingListReconciler.Reconcile([b, a], generated);

        Assert.Equal(forward.Update.Select(u => u.ItemId).Order(), backward.Update.Select(u => u.ItemId).Order());
        Assert.Equal(2, forward.Update.Count);
    }
}
