using Foodplanner.Domain.Quantities;
using Foodplanner.Domain.ShoppingLists;

namespace Foodplanner.Domain.Tests.ShoppingLists;

/// <summary>T-093: the pantry with amounts is subtracted from a generated list (<see cref="PantryFilter"/>).</summary>
public class PantrySubtractionTests
{
    private static readonly UnitCatalog Units = UnitCatalog.Seed;
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Cheese = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid Tortillas = Guid.Parse("00000000-0000-0000-0000-0000000000c2");
    private static readonly Guid Milk = Guid.Parse("00000000-0000-0000-0000-0000000000c3");
    private static readonly Guid Salt = Guid.Parse("00000000-0000-0000-0000-0000000000c4");
    private static readonly Guid Garlic = Guid.Parse("00000000-0000-0000-0000-0000000000c5");
    private static readonly Guid Tacos = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Pizza = Guid.Parse("10000000-0000-0000-0000-000000000002");

    private static Quantity Q(decimal amount, string unit) => new(amount, Units.Get(unit));

    private static Guid UnitId(string code) => Foodplanner.Domain.Catalog.UnitSeed.All.Single(r => r.Code == code).Id;

    private static GeneratedItem Item(Guid id, string name, params Quantity[] quantities) => new(id, name, quantities, [Tacos]);

    private static GeneratedList Plan(params GeneratedItem[] items) => new(items, []);

    private static GeneratedList Apply(GeneratedList list, params PantryEntry[] pantry) => PantryFilter.Apply(list, pantry, Now, Units);

    // ---- The example of the task ----------------------------------------------------------------------------------------

    private static GeneratedList TacoList() => Plan(Item(Cheese, "Cheese", Q(200, "g")), Item(Tortillas, "Tortillas", Q(8, "stk")));

    [Fact]
    public void Tacos_cheese_300_g_covers_200_g_and_tortillas_without_an_amount_are_checked()
    {
        var result = Apply(TacoList(), new PantryEntry(Cheese, null, Q(300, "g")), new PantryEntry(Tortillas, null));

        Assert.Empty(result.Items);
        Assert.Equal(["Cheese", "Tortillas"], result.CheckYouHave.Select(s => s.IngredientName));
        var cheese = result.CheckYouHave[0];
        Assert.Equal([Q(200, "g")], cheese.Needed);
        Assert.Equal(Q(300, "g"), cheese.AtHome!.Amount);
        var tortillas = result.CheckYouHave[1];
        Assert.Equal([Q(8, "stk")], tortillas.Needed);
        Assert.NotNull(tortillas.AtHome);
        Assert.Null(tortillas.AtHome.Amount);
    }

    [Fact]
    public void Tacos_cheese_100_g_buys_the_other_100_g_and_notes_what_is_at_home()
    {
        var result = Apply(TacoList(), new PantryEntry(Cheese, null, Q(100, "g")));

        Assert.Equal(["Cheese", "Tortillas"], result.Items.Select(i => i.IngredientName));
        var cheese = result.Items[0];
        Assert.Equal([Q(100, "g")], cheese.Quantities);
        Assert.Equal(Q(100, "g"), cheese.AtHome!.Amount);
        Assert.Null(result.Items[1].AtHome); // tortillas are not in the pantry
        Assert.Empty(result.CheckYouHave);
    }

    // ---- Full and partial cover -----------------------------------------------------------------------------------------

    [Fact]
    public void An_amount_exactly_equal_to_the_need_covers_it()
    {
        var result = Apply(Plan(Item(Cheese, "Cheese", Q(200, "g"))), new PantryEntry(Cheese, null, Q(200, "g")));

        Assert.Empty(result.Items);
        Assert.Single(result.CheckYouHave);
    }

    [Fact]
    public void Units_are_compared_in_the_base_unit_so_1_kg_covers_800_g()
    {
        var covered = Apply(Plan(Item(Cheese, "Cheese", Q(800, "g"))), new PantryEntry(Cheese, null, Q(1, "kg")));
        Assert.Empty(covered.Items);
        Assert.Equal(Q(1, "kg"), covered.CheckYouHave.Single().AtHome!.Amount);

        var partly = Apply(Plan(Item(Cheese, "Cheese", Q(1.5m, "kg"))), new PantryEntry(Cheese, null, Q(500, "g")));
        Assert.Equal([Q(1, "kg")], partly.Items.Single().Quantities); // 1500 g - 500 g, shown in kg
    }

    [Fact]
    public void The_rest_is_rounded_for_display_like_every_list_quantity()
    {
        var result = Apply(Plan(Item(Milk, "Milk", Q(1, "l"))), new PantryEntry(Milk, null, Q(3, "dl")));

        Assert.Equal([Q(700, "ml")], result.Items.Single().Quantities);
        Assert.Equal(Q(3, "dl"), result.Items.Single().AtHome!.Amount); // the note keeps the unit the household entered
    }

    [Fact]
    public void Count_units_only_match_the_same_unit()
    {
        var sameUnit = Apply(Plan(Item(Tortillas, "Tortillas", Q(8, "stk"))), new PantryEntry(Tortillas, null, Q(3, "stk")));
        Assert.Equal([Q(5, "stk")], sameUnit.Items.Single().Quantities);

        // A clove is not a piece (UnitConversion): nothing in the entry's dimension, so have-some-check.
        var otherUnit = Apply(Plan(Item(Garlic, "Garlic", Q(4, "fedd"))), new PantryEntry(Garlic, null, Q(2, "stk")));
        Assert.Empty(otherUnit.Items);
        Assert.Equal([Q(4, "fedd")], otherUnit.CheckYouHave.Single().Needed);
    }

    // ---- Different dimension and no amount -----------------------------------------------------------------------------

    [Fact]
    public void A_different_dimension_is_have_some_check_and_nothing_is_bought()
    {
        var result = Apply(Plan(Item(Cheese, "Cheese", Q(200, "g"))), new PantryEntry(Cheese, null, Q(2, "dl")));

        Assert.Empty(result.Items);
        var cheese = result.CheckYouHave.Single();
        Assert.Equal([Q(200, "g")], cheese.Needed);
        Assert.Equal(Q(2, "dl"), cheese.AtHome!.Amount);
    }

    [Fact]
    public void An_entry_without_an_amount_means_have_some_check()
    {
        var result = Apply(Plan(Item(Cheese, "Cheese", Q(200, "g"))), new PantryEntry(Cheese, null));

        Assert.Empty(result.Items);
        Assert.Null(result.CheckYouHave.Single().AtHome!.Amount);
    }

    [Fact]
    public void An_item_with_no_amount_to_subtract_from_is_checked_with_the_pantry_note()
    {
        // A weekly grocery without an amount: the pantry has some, so it is checked, not bought.
        var result = Apply(Plan(Item(Milk, "Milk")), new PantryEntry(Milk, null, Q(1, "l")));

        Assert.Empty(result.Items);
        var milk = result.CheckYouHave.Single();
        Assert.Empty(milk.Needed);
        Assert.Equal(Q(1, "l"), milk.AtHome!.Amount);
    }

    // ---- Expiry --------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_expired_entry_is_ignored_and_one_that_has_not_expired_counts()
    {
        var list = Plan(Item(Cheese, "Cheese", Q(200, "g")));

        var expired = Apply(list, new PantryEntry(Cheese, Now, Q(300, "g"))); // expires at the instant
        Assert.Same(list.Items[0], expired.Items.Single());
        Assert.Null(expired.Items[0].AtHome);

        var active = Apply(list, new PantryEntry(Cheese, Now.AddSeconds(1), Q(300, "g")));
        Assert.Empty(active.Items);
        Assert.Single(active.CheckYouHave);
    }

    // ---- Staples -------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_staple_with_an_active_entry_is_omitted_with_or_without_an_amount()
    {
        var list = new GeneratedList([], [new GeneratedStaple(Salt, "Salt", [Tacos]), new GeneratedStaple(Garlic, "Garlic", [Tacos])]);

        var result = Apply(list, new PantryEntry(Salt, null), new PantryEntry(Garlic, null, Q(500, "g")));

        Assert.Empty(result.CheckYouHave);
    }

    [Fact]
    public void A_staple_with_an_expired_entry_is_prompted_again()
    {
        var list = new GeneratedList([], [new GeneratedStaple(Salt, "Salt", [Tacos])]);

        var result = Apply(list, new PantryEntry(Salt, Now.AddDays(-1)));

        Assert.Single(result.CheckYouHave);
        Assert.Null(result.CheckYouHave[0].AtHome);
    }

    // ---- Several recipe lines of one ingredient ----------------------------------------------------------------------------

    [Fact]
    public void Lines_from_several_recipes_are_subtracted_from_their_merged_total()
    {
        // The generator already merged 200 g (tacos) + 150 g (pizza) = 350 g; the pantry has 300 g.
        var generated = ShoppingListGenerator.Generate(
            [
                Meal(Tacos, Planned(Cheese, "Cheese", 200, "g")),
                Meal(Pizza, Planned(Cheese, "Cheese", 150, "g")),
            ],
            Units);

        var result = Apply(generated, new PantryEntry(Cheese, null, Q(300, "g")));

        var cheese = result.Items.Single();
        Assert.Equal([Q(50, "g")], cheese.Quantities);
        Assert.Equal(new[] { Tacos, Pizza }.Order(), cheese.RecipeIds);
        Assert.Equal(Q(300, "g"), cheese.AtHome!.Amount);
    }

    [Fact]
    public void An_item_with_a_mass_and_a_volume_line_is_subtracted_in_the_dimension_the_entry_has()
    {
        var list = Plan(Item(Milk, "Milk", Q(200, "g"), Q(2, "dl")));

        var result = Apply(list, new PantryEntry(Milk, null, Q(500, "ml")));

        var milk = result.Items.Single();
        Assert.Equal([Q(200, "g")], milk.Quantities); // the volume line is covered, the mass line is still to buy
        Assert.Equal(Q(500, "ml"), milk.AtHome!.Amount);
    }

    [Fact]
    public void Everything_in_the_entrys_dimension_covered_and_a_to_taste_line_left_is_still_checked()
    {
        var list = Plan(Item(Cheese, "Cheese", Q(100, "g"), new Quantity(null, Units.Get("stk"))));

        var result = Apply(list, new PantryEntry(Cheese, null, Q(200, "g")));

        Assert.Empty(result.Items);
        Assert.Single(result.CheckYouHave);
    }

    // ---- Weekly groceries (T-092 overlay first, pantry second) -------------------------------------------------------------------------

    [Fact]
    public void A_weekly_grocery_is_subtracted_from_the_plan_plus_weekly_total()
    {
        var generated = Plan(Item(Milk, "Milk", Q(500, "ml")));
        var withWeekly = WeeklyGroceries.Overlay(generated, [new WeeklyEntry(Milk, Q(1, "l"))], Units); // 1.5 l

        var partly = Apply(withWeekly, new PantryEntry(Milk, null, Q(1, "l")));
        Assert.Equal([Q(500, "ml")], partly.Items.Single().Quantities);

        var covered = Apply(withWeekly, new PantryEntry(Milk, null, Q(2, "l")));
        Assert.Empty(covered.Items);
        Assert.Equal([Q(1.5m, "l")], covered.CheckYouHave.Single().Needed);
    }

    [Fact]
    public void A_weekly_staple_the_pantry_has_is_shown_under_check_you_have_not_hidden()
    {
        var list = new GeneratedList([], [new GeneratedStaple(Salt, "Salt", [Tacos])]);
        var withWeekly = WeeklyGroceries.Overlay(list, [new WeeklyEntry(Salt, null)], Units); // moves to the main list

        var result = Apply(withWeekly, new PantryEntry(Salt, null));

        Assert.Empty(result.Items);
        var salt = result.CheckYouHave.Single();
        Assert.Equal(Salt, salt.IngredientId);
        Assert.NotNull(salt.AtHome);
    }

    // ---- Shape of the result -------------------------------------------------------------------------------------------------

    [Fact]
    public void Items_without_an_entry_and_the_order_of_both_sections_are_unchanged()
    {
        var list = new GeneratedList(
            [Item(Cheese, "Cheese", Q(200, "g")), Item(Milk, "Milk", Q(1, "l")), Item(Tortillas, "Tortillas", Q(8, "stk"))],
            [new GeneratedStaple(Salt, "Salt", [Tacos])]);

        var result = Apply(list, new PantryEntry(Milk, null, Q(5, "l")));

        Assert.Equal(["Cheese", "Tortillas"], result.Items.Select(i => i.IngredientName));
        Assert.Equal(["Milk", "Salt"], result.CheckYouHave.Select(s => s.IngredientName)); // sorted by name again
    }

    [Fact]
    public void Applying_the_pantry_does_not_change_the_input_list()
    {
        var list = TacoList();

        _ = Apply(list, new PantryEntry(Cheese, null, Q(300, "g")));

        Assert.Equal(2, list.Items.Count);
        Assert.Empty(list.CheckYouHave);
    }

    // ---- Reconciler carries the note -----------------------------------------------------------------------------------------

    [Fact]
    public void The_reconciler_stores_the_note_and_the_need_of_a_covered_item_and_is_idempotent()
    {
        var generated = Apply(TacoList(), new PantryEntry(Cheese, null, Q(300, "g")));

        var first = ShoppingListReconciler.Reconcile([], generated);
        var cheese = first.Add.Single(a => a.IngredientId == Cheese);
        Assert.Equal(ShoppingSection.CheckYouHave, cheese.Section);
        Assert.Equal([new StoredQuantity(200, Units.Get("g").Id)], cheese.Quantities);
        Assert.Equal(new StoredAtHome(300, Units.Get("g").Id), cheese.AtHome);
        var tortillas = first.Add.Single(a => a.IngredientId == Tortillas);
        Assert.Equal(ShoppingSection.Main, tortillas.Section);
        Assert.Null(tortillas.AtHome);

        var stored = first.Add.Select(a => new ExistingGeneratedItem(Guid.NewGuid(), a.IngredientId, a.Section, false, a.Quantities, a.RecipeIds, a.AtHome));
        Assert.True(ShoppingListReconciler.Reconcile(stored, generated).IsEmpty);
    }

    [Fact]
    public void A_changed_pantry_amount_updates_the_note_without_flagging_a_checked_item()
    {
        var itemId = Guid.NewGuid();
        var gram = Units.Get("g").Id;
        var stored = new ExistingGeneratedItem(itemId, Cheese, ShoppingSection.Main, true, [new StoredQuantity(100, gram)], [Tacos], new StoredAtHome(100, gram));
        var generated = Apply(Plan(Item(Cheese, "Cheese", Q(200, "g"))), new PantryEntry(Cheese, null, Q(100, "g")));

        Assert.True(ShoppingListReconciler.Reconcile([stored], generated).IsEmpty);

        var noPantry = ShoppingListReconciler.Reconcile([stored], Plan(Item(Cheese, "Cheese", Q(200, "g"))));
        var update = Assert.Single(noPantry.Update);
        Assert.Null(update.AtHome!.Value); // the note is gone
        Assert.True(update.FlagQuantityChange); // 100 g -> 200 g on a checked item
    }

    [Fact]
    public void A_pantry_change_that_only_changes_the_note_does_not_flag_the_item()
    {
        var gram = Units.Get("g").Id;
        var stored = new ExistingGeneratedItem(Guid.NewGuid(), Cheese, ShoppingSection.Main, true, [new StoredQuantity(100, gram)], [Tacos], new StoredAtHome(100, gram));
        var generated = Apply(Plan(Item(Cheese, "Cheese", Q(200, "g"))), new PantryEntry(Cheese, null, Q(100, "g")));
        // The household had 100 g and now has 100 g more than it said: the buy amount is the same only when the pantry is the same.
        var changed = ShoppingListReconciler.Reconcile([stored with { AtHome = new StoredAtHome(null, null) }], generated);

        var update = Assert.Single(changed.Update);
        Assert.Equal(new StoredAtHome(100, gram), update.AtHome!.Value);
        Assert.False(update.FlagQuantityChange);
        Assert.Null(update.Quantities);
    }

    // ---- PantryItem ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_pantry_item_takes_an_amount_and_unit_together_or_neither()
    {
        var user = Guid.NewGuid();
        var unit = UnitId("g");

        var item = new PantryItem(Cheese, user, Now, null, 300, unit);
        Assert.Equal(300, item.Amount);
        Assert.Equal(unit, item.UnitId);

        Assert.Throws<ArgumentException>(() => new PantryItem(Cheese, user, Now, null, 300, null));
        Assert.Throws<ArgumentException>(() => new PantryItem(Cheese, user, Now, null, null, unit));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PantryItem(Cheese, user, Now, null, 0, unit));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PantryItem(Cheese, user, Now, null, -1, unit));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PantryItem(Cheese, user, Now, null, 100001, unit));
        Assert.Null(new PantryItem(Cheese, user, Now, null).Amount); // an old staple entry: no amount
    }

    [Fact]
    public void Confirming_again_replaces_the_amount_and_leaving_it_out_clears_it()
    {
        var user = Guid.NewGuid();
        var unit = UnitId("g");
        var item = new PantryItem(Cheese, user, Now, null, 300, unit);

        item.Confirm(user, Now.AddDays(1), null, 150, unit);
        Assert.Equal(150, item.Amount);

        item.Confirm(user, Now.AddDays(2), null);
        Assert.Null(item.Amount);
        Assert.Null(item.UnitId);
    }

    [Fact]
    public void Merging_two_pantry_rows_adds_amounts_in_the_same_unit_and_otherwise_prefers_an_amount()
    {
        var user = Guid.NewGuid();
        var gram = UnitId("g");
        var litre = UnitId("l");

        var target = new PantryItem(Cheese, user, Now, null, 300, gram);
        target.Absorb(new PantryItem(Milk, user, Now.AddDays(1), null, 200, gram));
        Assert.Equal(500, target.Amount);
        Assert.Equal(gram, target.UnitId);

        var differentUnit = new PantryItem(Cheese, user, Now, null, 300, gram);
        differentUnit.Absorb(new PantryItem(Milk, user, Now.AddDays(1), null, 1, litre)); // the newer confirmation wins
        Assert.Equal(1, differentUnit.Amount);
        Assert.Equal(litre, differentUnit.UnitId);

        var noAmountTarget = new PantryItem(Cheese, user, Now.AddDays(5), null);
        noAmountTarget.Absorb(new PantryItem(Milk, user, Now, null, 200, gram)); // the other has an amount, the newer has none
        Assert.Equal(200, noAmountTarget.Amount);
    }

    // ---- helpers for the generator -----------------------------------------------------------------------------------------

    private static PlannedMeal Meal(Guid recipe, params PlannedIngredient[] lines) => new(recipe, 4, 4, lines);

    private static PlannedIngredient Planned(Guid id, string name, decimal amount, string unit) =>
        new(id, name, false, Q(amount, unit));
}
