using CsCheck;
using Foodplanner.Domain.Quantities;
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

    private static ShoppingList Plan(params PlannedMeal[] meals) => ShoppingListGenerator.Generate(meals, Catalog);

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
    public void To_taste_is_kept_and_not_scaled()
    {
        var list = Plan(Meal(R1, 2, 8, Ing(MilkId, "Melk", Q(null, "ts"))));

        Assert.Equal([Q(null, "ts")], Assert.Single(list.Items).Quantities);
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
            return list.Items.All(i => i.IngredientId != SaltId)
                && list.Items.Select(i => i.IngredientId).Distinct().Count() == list.Items.Count
                && list.CheckYouHave.All(s => s.IngredientId == SaltId);
        });
    }

    private static bool Same(ShoppingList a, ShoppingList b) =>
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
