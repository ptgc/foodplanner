using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Quantities;
using Foodplanner.Domain.ShoppingLists;

namespace Foodplanner.Domain.Tests.ShoppingLists;

public class WeeklyGroceriesTests
{
    private static readonly UnitCatalog Units = UnitCatalog.Seed;
    private static readonly Guid Milk = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Bread = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid Salt = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid Rice = Guid.Parse("00000000-0000-0000-0000-00000000000d");
    private static readonly Guid Recipe = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Litre = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Gram = Guid.Parse("20000000-0000-0000-0000-000000000002");

    private static Quantity Q(decimal amount, string unit) => new(amount, Units.Get(unit));

    private static WeeklyGrocerySpec Spec(Guid ingredient, decimal? amount = null, Guid? unit = null) => new(ingredient, amount, unit);

    private static WeeklyItemState State(Guid ingredient, bool weekly = false, decimal? amount = null, Guid? unit = null) =>
        new(Guid.NewGuid(), ingredient, weekly, amount, unit);

    private static GeneratedList Generated(IEnumerable<GeneratedItem>? items = null, IEnumerable<GeneratedStaple>? staples = null) =>
        new([.. items ?? []], [.. staples ?? []]);

    // ---- Plan: the button ---------------------------------------------------------------------------------------

    [Fact]
    public void An_ingredient_with_no_item_is_added()
    {
        var plan = WeeklyGroceries.Plan([Spec(Milk, 1, Litre), Spec(Bread)], []);

        Assert.Equal(new[] { Milk, Bread }.Order(), plan.Add.Select(a => a.IngredientId));
        Assert.Empty(plan.Update);
    }

    [Fact]
    public void An_item_that_is_not_weekly_yet_gets_the_weekly_part()
    {
        var item = State(Milk);

        var plan = WeeklyGroceries.Plan([Spec(Milk, 1, Litre)], [item]);

        Assert.Empty(plan.Add);
        var update = Assert.Single(plan.Update);
        Assert.Equal((item.ItemId, 1m, Litre), (update.ItemId, update.Grocery.Amount, update.Grocery.UnitId));
    }

    [Fact]
    public void Pressing_again_changes_nothing_when_every_item_has_exactly_its_weekly_part()
    {
        var plan = WeeklyGroceries.Plan(
            [Spec(Milk, 1, Litre), Spec(Bread)],
            [State(Milk, true, 1, Litre), State(Bread, true)]);

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void An_edited_amount_replaces_the_weekly_part_and_removing_the_amount_counts_as_an_edit()
    {
        var milk = State(Milk, true, 1, Litre);
        var bread = State(Bread, true, 2, Gram);

        var plan = WeeklyGroceries.Plan([Spec(Milk, 2, Litre), Spec(Bread)], [milk, bread]);

        Assert.Empty(plan.Add);
        Assert.Equal(new[] { milk.ItemId, bread.ItemId }.Order(), plan.Update.Select(u => u.ItemId).Order());
    }

    [Fact]
    public void An_item_with_the_same_amount_in_another_unit_is_an_edit()
    {
        var plan = WeeklyGroceries.Plan([Spec(Milk, 1, Litre)], [State(Milk, true, 1, Gram)]);

        Assert.Single(plan.Update);
    }

    [Fact]
    public void An_item_that_was_removed_is_simply_missing_and_is_added_again()
    {
        var plan = WeeklyGroceries.Plan([Spec(Milk, 1, Litre), Spec(Bread)], [State(Milk, true, 1, Litre)]);

        Assert.Equal([Bread], plan.Add.Select(a => a.IngredientId));
        Assert.Empty(plan.Update);
    }

    [Fact]
    public void Items_that_are_not_weekly_groceries_are_left_alone_and_duplicates_in_the_set_count_once()
    {
        var plan = WeeklyGroceries.Plan([Spec(Milk), Spec(Milk, 3, Litre)], [State(Rice, false)]);

        Assert.Single(plan.Add);
        Assert.Empty(plan.Update);
    }

    [Fact]
    public void The_plan_does_not_depend_on_the_input_order()
    {
        var groceries = new[] { Spec(Milk), Spec(Bread), Spec(Salt) };

        var forward = WeeklyGroceries.Plan(groceries, []);
        var backward = WeeklyGroceries.Plan(groceries.Reverse(), []);

        Assert.Equal(forward.Add.Select(a => a.IngredientId), backward.Add.Select(a => a.IngredientId));
    }

    // ---- Overlay: generation ------------------------------------------------------------------------------------

    [Fact]
    public void Without_weekly_entries_the_generated_list_is_returned_as_it_is()
    {
        var generated = Generated([new GeneratedItem(Rice, "Ris", [Q(200, "g")], [Recipe])]);

        Assert.Same(generated, WeeklyGroceries.Overlay(generated, [], Units));
    }

    [Fact]
    public void A_weekly_amount_is_added_to_the_item_the_recipes_made()
    {
        var generated = Generated([new GeneratedItem(Milk, "Melk", [Q(500, "ml")], [Recipe])]);

        var result = WeeklyGroceries.Overlay(generated, [new WeeklyEntry(Milk, Q(1, "l"))], Units);

        var item = Assert.Single(result.Items);
        Assert.Equal([Q(1.5m, "l")], item.Quantities);
        Assert.Equal([Recipe], item.RecipeIds); // still "from the recipe"
    }

    [Fact]
    public void A_weekly_amount_of_another_dimension_is_an_extra_line_not_a_second_item()
    {
        var generated = Generated([new GeneratedItem(Milk, "Melk", [Q(500, "ml")], [Recipe])]);

        var result = WeeklyGroceries.Overlay(generated, [new WeeklyEntry(Milk, Q(300, "g"))], Units);

        var item = Assert.Single(result.Items);
        Assert.Equal([Q(300, "g"), Q(500, "ml")], item.Quantities);
    }

    [Fact]
    public void An_ingredient_the_plan_does_not_need_becomes_an_item_of_its_own_with_no_recipes()
    {
        var result = WeeklyGroceries.Overlay(Generated(), [new WeeklyEntry(Milk, Q(1, "l")), new WeeklyEntry(Bread, null)], Units);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal([Q(1, "l")], result.Items.Single(i => i.IngredientId == Milk).Quantities);
        Assert.Empty(result.Items.Single(i => i.IngredientId == Bread).Quantities);
        Assert.All(result.Items, i => Assert.Empty(i.RecipeIds));
    }

    [Fact]
    public void The_weekly_amount_is_shown_in_the_display_unit()
    {
        var result = WeeklyGroceries.Overlay(Generated(), [new WeeklyEntry(Milk, Q(10, "dl"))], Units);

        Assert.Equal([Q(1, "l")], result.Items.Single().Quantities);
    }

    [Fact]
    public void A_weekly_item_without_an_amount_leaves_the_recipe_amount_unchanged()
    {
        var generated = Generated([new GeneratedItem(Milk, "Melk", [Q(500, "ml")], [Recipe])]);

        var result = WeeklyGroceries.Overlay(generated, [new WeeklyEntry(Milk, null)], Units);

        Assert.Equal(generated.Items[0].Quantities, result.Items.Single().Quantities);
    }

    [Fact]
    public void A_staple_the_plan_only_wants_checked_moves_to_the_main_list()
    {
        var generated = Generated(staples: [new GeneratedStaple(Salt, "Salt", [Recipe]), new GeneratedStaple(Rice, "Ris", [Recipe])]);

        var result = WeeklyGroceries.Overlay(generated, [new WeeklyEntry(Salt, Q(1, "kg"))], Units);

        Assert.Equal([Rice], result.CheckYouHave.Select(s => s.IngredientId));
        var item = Assert.Single(result.Items);
        Assert.Equal(Salt, item.IngredientId);
        Assert.Equal([Recipe], item.RecipeIds);
        Assert.Equal([Q(1, "kg")], item.Quantities);
    }

    [Fact]
    public void Overlaying_twice_gives_the_same_result_and_does_not_double_the_amount()
    {
        var weekly = new[] { new WeeklyEntry(Milk, Q(1, "l")) };
        var generated = Generated([new GeneratedItem(Milk, "Melk", [Q(500, "ml")], [Recipe])]);

        var once = WeeklyGroceries.Overlay(generated, weekly, Units);
        var again = WeeklyGroceries.Overlay(generated, weekly, Units);

        Assert.Equal(once.Items.Single().Quantities, again.Items.Single().Quantities);
        Assert.Equal([Q(1.5m, "l")], again.Items.Single().Quantities);
    }

    [Fact]
    public void A_repeated_weekly_entry_counts_once()
    {
        var result = WeeklyGroceries.Overlay(Generated(), [new WeeklyEntry(Milk, Q(1, "l")), new WeeklyEntry(Milk, Q(1, "l"))], Units);

        Assert.Equal([Q(1, "l")], result.Items.Single().Quantities);
    }

    [Fact]
    public void The_reconciler_keeps_a_weekly_only_item_when_the_overlay_is_applied_and_drops_it_without()
    {
        var existing = new ExistingGeneratedItem(Guid.NewGuid(), Bread, ShoppingSection.Main, false, [], []);

        var withWeekly = ShoppingListReconciler.Reconcile([existing], WeeklyGroceries.Overlay(Generated(), [new WeeklyEntry(Bread, null)], Units));
        var without = ShoppingListReconciler.Reconcile([existing], Generated());

        Assert.True(withWeekly.IsEmpty);
        Assert.Equal([existing.ItemId], without.RemoveItemIds);
    }

    // ---- Entities -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, true)]
    [InlineData(0.0001, true)]
    [InlineData(100000, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(100000.5, false)]
    public void An_amount_is_valid_when_positive_and_within_the_recipe_limits(double amount, bool valid) =>
        Assert.Equal(valid, WeeklyGrocery.IsValidAmount((decimal)amount));

    [Fact]
    public void An_amount_with_more_than_four_decimals_is_invalid() =>
        Assert.False(WeeklyGrocery.IsValidAmount(1.00001m));

    [Fact]
    public void A_weekly_grocery_needs_an_ingredient_and_amount_and_unit_go_together()
    {
        Assert.Throws<ArgumentException>(() => new WeeklyGrocery(Guid.Empty, null, null));
        Assert.Throws<ArgumentException>(() => new WeeklyGrocery(Milk, 1, null));
        Assert.Throws<ArgumentException>(() => new WeeklyGrocery(Milk, null, Litre));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WeeklyGrocery(Milk, 0, Litre));
        var grocery = new WeeklyGrocery(Milk, 2, Litre);
        grocery.SetQuantity(null, null);
        Assert.Null(grocery.Amount);
    }

    [Fact]
    public void An_item_takes_a_weekly_part_and_gives_it_back()
    {
        var item = ShoppingListItem.Weekly(Guid.NewGuid(), Milk, 1, Litre);

        Assert.True(item.IsWeekly);
        Assert.Equal(ShoppingSection.Main, item.Section);
        Assert.False(item.SetWeekly(1, Litre)); // same again: nothing changed
        Assert.True(item.SetWeekly(2, Litre));
        Assert.Equal(2m, item.WeeklyAmount);
        Assert.True(item.ClearWeekly());
        Assert.False(item.ClearWeekly());
        Assert.Equal((false, null, null), (item.IsWeekly, item.WeeklyAmount, item.WeeklyUnitId));
    }

    [Fact]
    public void A_manual_item_cannot_be_a_weekly_grocery()
    {
        var item = ShoppingListItem.Manual(Guid.NewGuid(), "Kake");

        Assert.Throws<InvalidOperationException>(() => item.SetWeekly(null, null));
    }
}
