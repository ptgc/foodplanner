using CsCheck;
using Foodplanner.Domain.Quantities;
using Foodplanner.Domain.Recipes;
using Foodplanner.Domain.ShoppingLists;

namespace Foodplanner.Domain.Tests.ShoppingLists;

public class ShoppingListGeneratorTests
{
    private static readonly UnitCatalog Catalog = UnitCatalog.Seed;

    private static readonly Guid FlourId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid MilkId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid SaltId = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid EggId = Guid.Parse("00000000-0000-0000-0000-000000000004");
    private static readonly Guid R1 = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid R2 = Guid.Parse("10000000-0000-0000-0000-000000000002");

    private static Quantity Q(decimal? amount, string unit) => new(amount, Catalog.Get(unit));

    private static PlannedIngredient Ing(Guid id, string name, Quantity q, bool staple = false) => new(id, name, staple, q);

    private static PlannedMeal Meal(Guid recipe, int recipeServings, int planned, params PlannedIngredient[] ings) =>
        new(recipe, recipeServings, planned, ings);

    private static GeneratedList Plan(params PlannedMeal[] meals) => ShoppingListGenerator.Generate(meals, Catalog);

    [Theory]
    [InlineData(2, 4, 400)]
    [InlineData(4, 1, 50)]
    [InlineData(4, 3, 150)]
    public void Scaling_is_proportional(int recipeServings, int planned, int expectedGrams)
    {
        var list = Plan(Meal(R1, recipeServings, planned, Ing(FlourId, "Mel", Q(200m, "g"))));

        Assert.Equal([Q(expectedGrams, "g")], Assert.Single(list.Items).Quantities);
    }

    [Fact]
    public void Scaling_is_exact_before_rounding()
    {
        // 1 stk * 3/4 = 0.75 exactly.
        Assert.Equal(0.75m, ShoppingListGenerator.Scale(Q(1m, "stk"), Meal(R1, 4, 3)).Amount);

        // Three thirds of 100 g are exactly 100 g in total (merged exactly, rounded once).
        var third = Meal(R1, 3, 1, Ing(FlourId, "Mel", Q(100m, "g")));
        var list = Plan(third, third with { RecipeId = R2 }, third with { RecipeId = Guid.NewGuid() });
        Assert.Equal([Q(100m, "g")], Assert.Single(list.Items).Quantities);
    }

    [Fact]
    public void Rounding_happens_once_at_display_not_per_recipe()
    {
        // 0.2 stk twice: rounding each first would give 0.5 + 0.5 = 1; the exact sum 0.4 rounds to 0.5.
        var m = Meal(R1, 1, 1, Ing(EggId, "Egg", Q(0.2m, "stk")));
        var list = Plan(m, m with { RecipeId = R2 });

        Assert.Equal([Q(0.5m, "stk")], Assert.Single(list.Items).Quantities);
    }

    [Fact]
    public void To_taste_goes_to_check_you_have_without_a_quantity()
    {
        var list = Plan(Meal(R1, 2, 8, Ing(MilkId, "Melk", Q(null, "ts"))));

        Assert.Empty(list.Items);
        var item = Assert.Single(list.CheckYouHave);
        Assert.Equal(MilkId, item.IngredientId);
        Assert.Equal([R1], item.RecipeIds);
    }

    private static PlannedIngredient Line(
        Guid id, string name, AmountKind kind, decimal? amount, string unit, IngredientPurpose purpose = IngredientPurpose.Main, bool optional = false, bool staple = false) =>
        new(id, name, staple, Q(amount, unit), kind, purpose, optional);

    [Fact]
    public void As_needed_frying_fat_goes_to_check_you_have()
    {
        var list = Plan(Meal(R1, 4, 4, Line(MilkId, "Smør", AmountKind.AsNeeded, null, "stk", IngredientPurpose.Frying)));

        Assert.Empty(list.Items);
        Assert.Equal(MilkId, Assert.Single(list.CheckYouHave).IngredientId);
    }

    [Theory]
    [InlineData(3, "ss", true)] // 45 ml: still a few spoonfuls for the pan
    [InlineData(2, "dl", false)] // 200 ml: a real amount to buy
    [InlineData(1, "ts", true)]
    [InlineData(45, "ml", true)]
    [InlineData(46, "ml", false)]
    public void Measured_frying_fat_up_to_three_tablespoons_is_only_checked(int amount, string unit, bool checkOnly)
    {
        var list = Plan(Meal(R1, 1, 1, Line(MilkId, "Olje", AmountKind.Measured, amount, unit, IngredientPurpose.Frying)));

        Assert.Equal(checkOnly, list.Items.Count == 0);
        Assert.Equal(checkOnly, list.CheckYouHave.Count == 1);
    }

    [Fact]
    public void Frying_fat_is_judged_after_scaling()
    {
        // 2 ss for 2 servings is 6 ss (90 ml) for 6: a real amount.
        var list = Plan(Meal(R1, 2, 6, Line(MilkId, "Olje", AmountKind.Measured, 2m, "ss", IngredientPurpose.Frying)));

        Assert.Single(list.Items);
    }

    [Fact]
    public void A_measured_frying_amount_in_grams_is_still_bought()
    {
        var list = Plan(Meal(R1, 1, 1, Line(MilkId, "Smør", AmountKind.Measured, 20m, "g", IngredientPurpose.Frying)));

        Assert.Equal([Q(20m, "g")], Assert.Single(list.Items).Quantities);
    }

    [Fact]
    public void Optional_lines_are_only_checked()
    {
        var list = Plan(Meal(R1, 1, 1, Line(MilkId, "Ost", AmountKind.Measured, 1m, "dl", optional: true)));

        Assert.Empty(list.Items);
        Assert.Equal(MilkId, Assert.Single(list.CheckYouHave).IngredientId);
    }

    [Fact]
    public void Another_recipe_that_needs_the_amount_keeps_the_ingredient_on_the_main_list_with_only_that_amount()
    {
        var list = Plan(
            Meal(R1, 4, 4, Line(MilkId, "Smør", AmountKind.AsNeeded, null, "stk", IngredientPurpose.Frying), Line(FlourId, "Mel", AmountKind.Measured, 1m, "dl")),
            Meal(R2, 4, 8, Line(MilkId, "Smør", AmountKind.Measured, 100m, "g")));

        var butter = Assert.Single(list.Items, i => i.IngredientId == MilkId);
        Assert.Equal([Q(200m, "g")], butter.Quantities);
        Assert.Equal([R1, R2], butter.RecipeIds); // both recipes use it
        Assert.DoesNotContain(list.CheckYouHave, s => s.IngredientId == MilkId);
    }

    [Fact]
    public void To_taste_and_as_needed_are_never_scaled()
    {
        foreach (var kind in new[] { AmountKind.ToTaste, AmountKind.AsNeeded })
        {
            var scaled = ShoppingListGenerator.Scale(kind, Q(null, "stk"), Meal(R1, 2, 100));
            Assert.Null(scaled.Amount);
        }

        Assert.Equal(300m, ShoppingListGenerator.Scale(AmountKind.Measured, Q(6m, "ss"), Meal(R1, 2, 100)).Amount);
    }

    [Fact]
    public void An_amount_on_a_to_taste_line_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Line(MilkId, "Salt", AmountKind.ToTaste, 1m, "ts"));
        Assert.Throws<ArgumentException>(() => Line(MilkId, "Salt", AmountKind.Measured, null, "ts"));
    }

    [Fact]
    public void Staples_are_never_in_the_main_list()
    {
        var list = Plan(
            Meal(R1, 2, 2, Ing(SaltId, "Salt", Q(null, "ts"), staple: true), Ing(FlourId, "Mel", Q(100m, "g"))),
            Meal(R2, 2, 4, Ing(SaltId, "Salt", Q(1m, "ts"), staple: true)));

        Assert.Equal([FlourId], list.Items.Select(i => i.IngredientId));
        var salt = Assert.Single(list.CheckYouHave);
        Assert.Equal(SaltId, salt.IngredientId);
        Assert.Equal([R1, R2], salt.RecipeIds);
    }

    [Fact]
    public void Same_ingredient_across_recipes_is_merged_with_contributing_recipes()
    {
        var list = Plan(
            Meal(R2, 2, 2, Ing(FlourId, "Mel", Q(0.3m, "kg"))),
            Meal(R1, 2, 2, Ing(FlourId, "Mel", Q(200m, "g"))));

        var item = Assert.Single(list.Items);
        Assert.Equal([Q(500m, "g")], item.Quantities);
        Assert.Equal([R1, R2], item.RecipeIds);
        Assert.Equal("Mel", item.IngredientName);
    }

    [Fact]
    public void Mass_and_volume_of_the_same_ingredient_stay_separate()
    {
        var list = Plan(Meal(R1, 1, 1, Ing(MilkId, "Melk", Q(200m, "g")), Ing(MilkId, "Melk", Q(2m, "dl"))));

        Assert.Equal([Q(200m, "g"), Q(200m, "ml")], Assert.Single(list.Items).Quantities);
    }

    [Fact]
    public void Same_recipe_planned_twice_lists_the_recipe_once_and_adds_quantities()
    {
        var m = Meal(R1, 2, 2, Ing(FlourId, "Mel", Q(100m, "g")));
        var item = Assert.Single(Plan(m, m).Items);

        Assert.Equal([Q(200m, "g")], item.Quantities);
        Assert.Equal([R1], item.RecipeIds);
    }

    [Fact]
    public void Empty_plan_gives_empty_list()
    {
        var list = Plan();

        Assert.Empty(list.Items);
        Assert.Empty(list.CheckYouHave);
        Assert.Empty(Plan(Meal(R1, 2, 2)).Items);
    }

    [Fact]
    public void Order_is_by_name_ignoring_case_with_norwegian_letters_after_z()
    {
        var list = Plan(Meal(R1, 1, 1,
            Ing(Guid.NewGuid(), "Åpen", Q(1m, "stk")),
            Ing(Guid.NewGuid(), "zebra", Q(1m, "stk")),
            Ing(Guid.NewGuid(), "Banan", Q(1m, "stk")),
            Ing(Guid.NewGuid(), "ananas", Q(1m, "stk"))));

        Assert.Equal(["ananas", "Banan", "zebra", "Åpen"], list.Items.Select(i => i.IngredientName));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -3)]
    public void Non_positive_servings_are_rejected(int recipeServings, int planned) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Meal(R1, recipeServings, planned));

    [Fact]
    public void Invalid_ingredient_is_rejected() =>
        Assert.ThrowsAny<ArgumentException>(() => Ing(FlourId, " ", Q(1m, "g")));

    // ---- properties ----

    private static readonly Guid[] Ids = [FlourId, MilkId, SaltId, EggId];
    private static readonly string[] Names = ["Mel", "Melk", "Salt", "Egg"];
    private static readonly string[] Codes = ["g", "kg", "ml", "dl", "l", "ts", "ss", "stk", "fedd"];

    private static readonly Gen<PlannedIngredient> AnyIngredient = Gen.Select(
        Gen.Int[0, 3], Gen.Int[0, 5000], Gen.Int[0, 4], Gen.OneOf(Codes),
        (i, hundredths, die, code) =>
            new PlannedIngredient(Ids[i], Names[i], i == 2, new Quantity(die == 0 ? null : hundredths / 100m, Catalog.Get(code))));

    private static readonly Gen<PlannedMeal> AnyMeal = Gen.Select(
        Gen.Int[0, 1], Gen.Int[1, 6], Gen.Int[1, 8], AnyIngredient.List[0, 6],
        (r, rs, ps, ings) => new PlannedMeal(r == 0 ? R1 : R2, rs, ps, ings));

    [Fact]
    public void Property_output_is_independent_of_input_order()
    {
        Gen.Select(AnyMeal.List[0, 5], Gen.Int, (meals, seed) => (meals, seed)).Sample(
            t =>
            {
                var shuffled = t.meals.ToArray();
                new Random(t.seed).Shuffle(shuffled);
                var reshuffled = shuffled.Select(m =>
                {
                    var ings = m.Ingredients.ToArray();
                    new Random(t.seed).Shuffle(ings);
                    return new PlannedMeal(m.RecipeId, m.RecipeServings, m.PlannedServings, ings);
                }).ToArray();
                return Same(ShoppingListGenerator.Generate(t.meals, Catalog), ShoppingListGenerator.Generate(reshuffled, Catalog));
            },
            iter: 1000);
    }

    [Fact]
    public void Property_staples_never_appear_in_items_and_each_ingredient_appears_once()
    {
        AnyMeal.List[0, 5].Sample(meals =>
        {
            var list = ShoppingListGenerator.Generate(meals, Catalog);
            var checkIds = list.CheckYouHave.Select(s => s.IngredientId).ToList();
            return list.Items.All(i => i.IngredientId != SaltId)
                && list.Items.Select(i => i.IngredientId).Distinct().Count() == list.Items.Count
                && checkIds.Distinct().Count() == checkIds.Count
                && !list.Items.Any(i => checkIds.Contains(i.IngredientId)) // an ingredient is in one section
                && list.Items.All(i => i.Quantities.All(q => q.Amount is not null)) // to taste is never bought by amount
                && (!meals.Any(m => m.Ingredients.Any(x => x.IngredientId == SaltId)) || checkIds.Contains(SaltId));
        });
    }

    private static bool Same(GeneratedList a, GeneratedList b) =>
        a.Items.Count == b.Items.Count
        && a.CheckYouHave.Count == b.CheckYouHave.Count
        && a.Items.Zip(b.Items).All(p =>
            p.First.IngredientId == p.Second.IngredientId
            && p.First.IngredientName == p.Second.IngredientName
            && p.First.Quantities.SequenceEqual(p.Second.Quantities)
            && p.First.RecipeIds.SequenceEqual(p.Second.RecipeIds))
        && a.CheckYouHave.Zip(b.CheckYouHave).All(p =>
            p.First.IngredientId == p.Second.IngredientId && p.First.RecipeIds.SequenceEqual(p.Second.RecipeIds));
}
