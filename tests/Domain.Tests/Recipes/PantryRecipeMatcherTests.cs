using Foodplanner.Domain.Quantities;
using Foodplanner.Domain.Recipes;
using Foodplanner.Domain.ShoppingLists;

namespace Foodplanner.Domain.Tests.Recipes;

/// <summary>T-100 (Q-029): the pure "what can I make with what we have" matcher.</summary>
public sealed class PantryRecipeMatcherTests
{
    private static readonly UnitCatalog Units = UnitCatalog.Seed;
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 4);

    private static readonly Guid Cheese = Guid.NewGuid();
    private static readonly Guid Tortillas = Guid.NewGuid();
    private static readonly Guid Milk = Guid.NewGuid();
    private static readonly Guid Flour = Guid.NewGuid();
    private static readonly Guid Butter = Guid.NewGuid();
    private static readonly Guid Margarine = Guid.NewGuid();
    private static readonly Guid Oil = Guid.NewGuid();
    private static readonly Guid Salt = Guid.NewGuid();
    private static readonly Guid Chives = Guid.NewGuid();
    private static readonly Guid Garlic = Guid.NewGuid();

    private static Quantity Q(decimal amount, string unit) => new(amount, Units.Get(unit));

    private static LineOption Opt(Guid id, bool staple = false) => new(id, staple);

    private static RecipeLine Line(Guid id, decimal amount, string unit, params LineOption[] alternatives) =>
        new([Opt(id), .. alternatives], AmountKind.Measured, Q(amount, unit));

    private static RecipeLine Staple(Guid id, decimal amount, string unit) =>
        new([Opt(id, staple: true)], AmountKind.Measured, Q(amount, unit));

    private static RecipeCandidate Recipe(string title, int servings, params RecipeLine[] lines) =>
        Candidate(title, servings, 0, null, lines);

    private static RecipeCandidate Candidate(string title, int servings, int likes, DateOnly? last, params RecipeLine[] lines) =>
        new(Guid.NewGuid(), title, FoodType.Vegetarian, servings, lines, likes, last);

    private static PantryEntry Have(Guid id, decimal? amount = null, string unit = "g", DateTimeOffset? expires = null) =>
        new(id, expires, amount is { } a ? Q(a, unit) : null);

    private static IReadOnlyList<RecipeMatch> Suggest(
        RecipeCandidate[] recipes, PantryEntry[] pantry, int? servings = null, IReadOnlyDictionary<Guid, decimal>? densities = null) =>
        PantryRecipeMatcher.Suggest(recipes, pantry, Now, Today, Units, servings, densities);

    private static RecipeMatch Single(RecipeCandidate recipe, PantryEntry[] pantry, int? servings = null, IReadOnlyDictionary<Guid, decimal>? densities = null) =>
        PantryRecipeMatcher.Match(recipe, pantry, Now, Units, servings, densities);

    [Fact]
    public void A_full_match_has_nothing_missing()
    {
        var tacos = Recipe("Tacos", 4, Line(Cheese, 200, "g"), Line(Tortillas, 8, "stk"));

        var match = Suggest([tacos], [Have(Cheese, 300), Have(Tortillas, 8, "stk")]).Single();

        Assert.Equal(0, match.MissingCount);
        Assert.Equal(4, match.Servings);
    }

    [Fact]
    public void Missing_one_two_and_three_things_three_is_left_out()
    {
        var one = Recipe("One", 4, Line(Cheese, 200, "g"), Line(Tortillas, 8, "stk"), Line(Milk, 2, "dl"));
        var two = Recipe("Two", 4, Line(Cheese, 200, "g"), Line(Tortillas, 8, "stk"), Line(Milk, 2, "dl"));
        var three = Recipe("Three", 4, Line(Cheese, 200, "g"), Line(Tortillas, 8, "stk"), Line(Milk, 2, "dl"));

        Assert.Equal(1, Suggest([one], [Have(Cheese), Have(Tortillas)]).Single().MissingCount);
        Assert.Equal(2, Suggest([two], [Have(Cheese)]).Single().MissingCount);
        Assert.Empty(Suggest([three], []));
    }

    [Fact]
    public void A_not_at_home_ingredient_is_missing_with_its_whole_amount()
    {
        var tacos = Recipe("Tacos", 4, Line(Cheese, 200, "g"));

        var missing = Suggest([tacos], []).Single().Missing.Single();

        Assert.Equal(Cheese, missing.IngredientId);
        Assert.Equal(MissingReason.NotAtHome, missing.Reason);
        Assert.Equal(Q(200, "g"), missing.Quantities.Single());
    }

    [Fact]
    public void A_pantry_entry_without_an_amount_satisfies_any_amount()
    {
        var tacos = Recipe("Tacos", 4, Line(Cheese, 5000, "g"), Line(Milk, 10, "l"));

        Assert.Equal(0, Suggest([tacos], [Have(Cheese), Have(Milk)]).Single().MissingCount);
    }

    [Fact]
    public void A_pantry_amount_that_is_too_small_is_missing_by_the_shortfall()
    {
        var tacos = Recipe("Tacos", 4, Line(Cheese, 300, "g"));

        var missing = Suggest([tacos], [Have(Cheese, 100)]).Single().Missing.Single();

        Assert.Equal(MissingReason.NotEnough, missing.Reason);
        Assert.Equal(Q(200, "g"), missing.Quantities.Single());
    }

    [Fact]
    public void A_pantry_amount_that_exactly_covers_the_need_is_enough()
    {
        var tacos = Recipe("Tacos", 4, Line(Cheese, 200, "g"));

        Assert.Equal(0, Suggest([tacos], [Have(Cheese, 0.2m, "kg")]).Single().MissingCount);
    }

    [Fact]
    public void Units_in_the_same_dimension_are_compared_in_the_base_unit()
    {
        var soup = Recipe("Soup", 4, Line(Milk, 5, "dl"));

        Assert.Equal(0, Suggest([soup], [Have(Milk, 1, "l")]).Single().MissingCount);
        var short_ = Suggest([soup], [Have(Milk, 2, "dl")]).Single().Missing.Single();
        Assert.Equal(Q(300, "ml"), short_.Quantities.Single());
    }

    [Fact]
    public void Mass_and_volume_do_not_convert_without_a_density_so_the_whole_need_is_missing()
    {
        var soup = Recipe("Soup", 4, Line(Milk, 2, "dl"));

        var missing = Suggest([soup], [Have(Milk, 1000, "g")]).Single().Missing.Single();

        Assert.Equal(MissingReason.UnitMismatch, missing.Reason);
        Assert.Equal(Q(200, "ml"), missing.Quantities.Single());
    }

    [Fact]
    public void Mass_and_volume_convert_with_a_density()
    {
        var soup = Recipe("Soup", 4, Line(Milk, 2, "dl")); // 200 ml
        var density = new Dictionary<Guid, decimal> { [Milk] = 1.03m }; // g per ml

        Assert.Equal(0, Suggest([soup], [Have(Milk, 300, "g")], densities: density).Single().MissingCount); // 300 g = ~291 ml
        var missing = Suggest([soup], [Have(Milk, 103, "g")], densities: density).Single().Missing.Single(); // 100 ml, 100 ml short
        Assert.Equal(MissingReason.NotEnough, missing.Reason);
        Assert.Equal(Q(100, "ml"), missing.Quantities.Single());
    }

    [Fact]
    public void A_volume_in_the_pantry_covers_a_mass_line_with_a_density()
    {
        var bake = Recipe("Bake", 4, Line(Flour, 120, "g"));
        var density = new Dictionary<Guid, decimal> { [Flour] = 0.6m };

        Assert.Equal(0, Suggest([bake], [Have(Flour, 2, "dl")], densities: density).Single().MissingCount); // 200 ml = 120 g
        Assert.Equal(1, Suggest([bake], [Have(Flour, 1, "dl")], densities: density).Single().MissingCount);
    }

    [Fact]
    public void A_count_unit_only_matches_itself()
    {
        var soup = Recipe("Soup", 4, Line(Garlic, 4, "fedd"));

        Assert.Equal(0, Suggest([soup], [Have(Garlic, 6, "fedd")]).Single().MissingCount);
        Assert.Equal(MissingReason.UnitMismatch, Suggest([soup], [Have(Garlic, 6, "stk")]).Single().Missing.Single().Reason);
    }

    [Fact]
    public void Amounts_are_scaled_to_the_chosen_servings()
    {
        var tacos = Recipe("Tacos", 4, Line(Cheese, 200, "g"));

        Assert.Equal(0, Suggest([tacos], [Have(Cheese, 200)]).Single().MissingCount); // own servings
        var for8 = Suggest([tacos], [Have(Cheese, 200)], servings: 8).Single();
        Assert.Equal(8, for8.Servings);
        Assert.Equal(Q(200, "g"), for8.Missing.Single().Quantities.Single());
        Assert.Equal(0, Suggest([tacos], [Have(Cheese, 100)], servings: 2).Single().MissingCount);
    }

    [Fact]
    public void An_expired_pantry_entry_does_not_count_and_one_expiring_later_does()
    {
        var tacos = Recipe("Tacos", 4, Line(Cheese, 200, "g"));

        Assert.Equal(1, Suggest([tacos], [Have(Cheese, 300, expires: Now.AddDays(-1))]).Single().MissingCount);
        Assert.Equal(1, Suggest([tacos], [Have(Cheese, 300, expires: Now)]).Single().MissingCount); // expires now = no longer active
        Assert.Equal(0, Suggest([tacos], [Have(Cheese, 300, expires: Now.AddDays(1))]).Single().MissingCount);
    }

    [Fact]
    public void A_line_with_alternatives_is_satisfied_by_any_alternative_at_home()
    {
        var bake = Recipe("Bake", 4, Line(Butter, 100, "g", Opt(Margarine), Opt(Oil)));

        Assert.Equal(0, Suggest([bake], [Have(Margarine)]).Single().MissingCount);
        Assert.Equal(0, Suggest([bake], [Have(Margarine, 500)]).Single().MissingCount);
        Assert.Equal(1, Suggest([bake], [Have(Margarine, 50)]).Single().MissingCount); // not enough of the only alternative at home
    }

    [Fact]
    public void An_alternative_in_a_different_dimension_without_density_does_not_satisfy_the_line()
    {
        var bake = Recipe("Bake", 4, Line(Butter, 100, "g", Opt(Oil)));

        var missing = Suggest([bake], [Have(Oil, 5, "dl")]).Single().Missing.Single();

        Assert.Equal(Butter, missing.IngredientId);
        Assert.Equal([Oil], missing.AlternativeIds);
    }

    [Fact]
    public void A_line_with_no_alternative_at_home_is_one_missing_thing_reported_on_the_own_ingredient()
    {
        var bake = Recipe("Bake", 4, Line(Butter, 100, "g", Opt(Margarine), Opt(Oil)));

        var match = Suggest([bake], []).Single();

        var missing = Assert.Single(match.Missing);
        Assert.Equal(Butter, missing.IngredientId);
        Assert.Equal([Margarine, Oil], missing.AlternativeIds);
    }

    [Fact]
    public void An_alternative_that_is_a_staple_satisfies_the_line()
    {
        var bake = Recipe("Bake", 4, Line(Butter, 100, "g", Opt(Margarine, staple: true)));

        Assert.Equal(0, Suggest([bake], []).Single().MissingCount);
    }

    [Fact]
    public void To_taste_as_needed_and_optional_lines_are_never_missing()
    {
        var recipe = Recipe(
            "Pasta",
            4,
            Line(Cheese, 100, "g"),
            new RecipeLine([Opt(Salt)], AmountKind.ToTaste, null),
            new RecipeLine([Opt(Oil)], AmountKind.AsNeeded, null, IngredientPurpose.Frying),
            new RecipeLine([Opt(Chives)], AmountKind.Measured, Q(1, "stk"), IngredientPurpose.Garnish, isOptional: true));

        Assert.Equal(0, Suggest([recipe], [Have(Cheese)]).Single().MissingCount);
    }

    [Fact]
    public void Small_frying_fat_is_never_missing_but_a_large_amount_or_grams_are()
    {
        var small = Recipe("Small", 4, Line(Cheese, 100, "g"), new RecipeLine([Opt(Oil)], AmountKind.Measured, Q(3, "ss"), IngredientPurpose.Frying));
        var large = Recipe("Large", 4, Line(Cheese, 100, "g"), new RecipeLine([Opt(Oil)], AmountKind.Measured, Q(2, "dl"), IngredientPurpose.Frying));
        var inGrams = Recipe("Grams", 4, Line(Cheese, 100, "g"), new RecipeLine([Opt(Butter)], AmountKind.Measured, Q(20, "g"), IngredientPurpose.Frying));
        var pantry = new[] { Have(Cheese) };

        Assert.Equal(0, Suggest([small], pantry).Single().MissingCount); // 45 ml, the limit
        Assert.Equal(Oil, Suggest([large], pantry).Single().Missing.Single().IngredientId);
        Assert.Equal(Butter, Suggest([inGrams], pantry).Single().Missing.Single().IngredientId);
    }

    [Fact]
    public void Frying_fat_is_judged_after_scaling()
    {
        var recipe = Recipe("Fry", 2, new RecipeLine([Opt(Oil)], AmountKind.Measured, Q(2, "ss"), IngredientPurpose.Frying)); // 30 ml for 2

        Assert.Equal(0, Suggest([recipe], [], servings: 2).Single().MissingCount);
        Assert.Equal(1, Suggest([recipe], [], servings: 4).Single().MissingCount); // 60 ml
    }

    [Fact]
    public void A_staple_counts_as_at_home_even_with_a_big_amount()
    {
        var bake = Recipe("Bake", 4, Staple(Flour, 5, "kg"), Line(Cheese, 100, "g"));

        Assert.Equal(0, Suggest([bake], [Have(Cheese)]).Single().MissingCount);
    }

    [Fact]
    public void One_pantry_amount_is_used_up_across_lines_of_the_same_ingredient()
    {
        var recipe = Recipe("Gratin", 4, Line(Cheese, 100, "g"), Line(Cheese, 100, "g"));

        var match = Suggest([recipe], [Have(Cheese, 150)]).Single();

        var missing = Assert.Single(match.Missing); // one thing, 50 g short
        Assert.Equal(Q(50, "g"), missing.Quantities.Single());
        Assert.Equal(0, Suggest([recipe], [Have(Cheese, 200)]).Single().MissingCount);
    }

    [Fact]
    public void Two_missing_lines_of_one_ingredient_count_as_one_thing_with_the_amounts_added()
    {
        var recipe = Recipe("Gratin", 4, Line(Cheese, 100, "g"), Line(Cheese, 0.1m, "kg"), Line(Milk, 1, "dl"), Line(Butter, 10, "g"));

        var match = Suggest([recipe], [Have(Milk), Have(Butter)]).Single();

        var missing = Assert.Single(match.Missing);
        Assert.Equal(Q(200, "g"), missing.Quantities.Single());
    }

    [Fact]
    public void A_recipe_without_ingredient_lines_is_never_suggested()
    {
        Assert.Empty(Suggest([Recipe("Empty", 4)], [Have(Cheese)]));
    }

    [Fact]
    public void Ranking_is_fewest_missing_then_likes_then_not_recent_then_name()
    {
        var recent = Today.AddDays(-3);
        var old = Today.AddDays(-30);
        var pantry = new[] { Have(Cheese), Have(Tortillas) };
        var oneMissing = Candidate("A one missing", 4, 9, null, Line(Cheese, 1, "g"), Line(Milk, 1, "dl"));
        var liked = Candidate("Z liked", 4, 3, old, Line(Cheese, 1, "g"));
        var likedRecent = Candidate("Y liked but recent", 4, 3, recent, Line(Cheese, 1, "g"));
        var fresh = Candidate("B fresh", 4, 0, null, Line(Cheese, 1, "g"));
        var old2 = Candidate("C old", 4, 0, old, Line(Cheese, 1, "g"));
        var recent2 = Candidate("A recent", 4, 0, recent, Line(Cheese, 1, "g"));
        var future = Candidate("D future", 4, 0, Today.AddDays(2), Line(Cheese, 1, "g"));

        var titles = Suggest([oneMissing, recent2, old2, fresh, likedRecent, liked, future], pantry).Select(m => m.Recipe.Title).ToArray();

        // 0 missing: likes first (liked and likedRecent tie on likes: the not-recent one goes first), then not recent by name
        // (a future plan does not count as recent), then recent ones; the one-missing recipe comes last whatever its likes.
        Assert.Equal(["Z liked", "Y liked but recent", "B fresh", "C old", "D future", "A recent", "A one missing"], titles);
    }

    [Fact]
    public void Equal_recipes_are_ordered_by_name_ignoring_case_then_id_so_the_order_is_stable()
    {
        var b = Candidate("banana bread", 4, 0, null, Line(Cheese, 1, "g"));
        var a = Candidate("Apple pie", 4, 0, null, Line(Cheese, 1, "g"));
        var pantry = new[] { Have(Cheese) };

        Assert.Equal(["Apple pie", "banana bread"], Suggest([b, a], pantry).Select(m => m.Recipe.Title).ToArray());
        Assert.Equal(["Apple pie", "banana bread"], Suggest([a, b], pantry).Select(m => m.Recipe.Title).ToArray());
    }

    [Fact]
    public void Recent_means_planned_in_the_last_14_days_including_today()
    {
        RecipeCandidate At(DateOnly? d) => Candidate("x", 4, 0, d, Line(Cheese, 1, "g"));

        Assert.True(PantryRecipeMatcher.IsRecent(At(Today), Today));
        Assert.True(PantryRecipeMatcher.IsRecent(At(Today.AddDays(-14)), Today));
        Assert.False(PantryRecipeMatcher.IsRecent(At(Today.AddDays(-15)), Today));
        Assert.False(PantryRecipeMatcher.IsRecent(At(Today.AddDays(1)), Today));
        Assert.False(PantryRecipeMatcher.IsRecent(At(null), Today));
    }

    [Fact]
    public void Match_returns_every_missing_thing_without_the_cutoff()
    {
        var recipe = Recipe("Big", 4, Line(Cheese, 1, "g"), Line(Milk, 1, "dl"), Line(Butter, 1, "g"));

        Assert.Equal(3, Single(recipe, []).MissingCount);
    }

    [Fact]
    public void A_measured_line_must_have_an_amount_and_the_others_must_not()
    {
        Assert.Throws<ArgumentException>(() => new RecipeLine([Opt(Cheese)], AmountKind.Measured, null));
        Assert.Throws<ArgumentException>(() => new RecipeLine([Opt(Cheese)], AmountKind.ToTaste, Q(1, "g")));
        Assert.Throws<ArgumentException>(() => new RecipeLine([], AmountKind.ToTaste, null));
    }
}
