using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Quantities;
using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.ShoppingLists;

/// <summary>
/// What the household's pantry holds of an ingredient on the list (T-093). <see cref="Amount"/> null = "we have some", no
/// amount known. Set only by <see cref="PantryFilter"/>.
/// </summary>
public sealed record AtHome(Quantity? Amount);

/// <summary>
/// A buyable ingredient: merged quantities for display (see <see cref="QuantityMerger.ForDisplay"/>) and the recipes that
/// contributed. <see cref="AtHome"/> is set when the pantry has some (then <see cref="Quantities"/> is what is left to buy).
/// </summary>
public sealed record GeneratedItem(
    Guid IngredientId,
    string IngredientName,
    IReadOnlyList<Quantity> Quantities,
    IReadOnlyList<Guid> RecipeIds,
    AtHome? AtHome)
{
    public GeneratedItem(Guid ingredientId, string ingredientName, IReadOnlyList<Quantity> quantities, IReadOnlyList<Guid> recipeIds)
        : this(ingredientId, ingredientName, quantities, recipeIds, null)
    {
    }
}

/// <summary>
/// An ingredient the person only checks they have: a staple the plan uses (no quantity), or (T-093) an item the pantry
/// covers; then <see cref="Needed"/> is what the plan needs and <see cref="AtHome"/> what the pantry holds.
/// </summary>
public sealed record GeneratedStaple(
    Guid IngredientId, string IngredientName, IReadOnlyList<Guid> RecipeIds, IReadOnlyList<Quantity> Needed, AtHome? AtHome)
{
    public GeneratedStaple(Guid ingredientId, string ingredientName, IReadOnlyList<Guid> recipeIds)
        : this(ingredientId, ingredientName, recipeIds, [], null)
    {
    }
}

public sealed record GeneratedList(IReadOnlyList<GeneratedItem> Items, IReadOnlyList<GeneratedStaple> CheckYouHave);

/// <summary>
/// Pure generation of a shopping list from planned meals.
///  1. Each quantity is scaled by PlannedServings / RecipeServings, exactly (decimal; multiply first, then divide).
///     To-taste stays to-taste.
///  2. Entries are grouped by ingredient id. An ingredient flagged staple in ANY entry is a staple.
///  3. Staples go only to <see cref="GeneratedList.CheckYouHave"/>, never to the main list, even with a quantity.
///  4. Other ingredients: only the lines that <see cref="NeedsBuying"/> (Measured, not optional, not a few spoonfuls of
///     frying fat) are merged with <see cref="QuantityMerger.Merge"/> (exact) and then <see cref="QuantityMerger.ForDisplay"/>.
///     Rounding happens once, at the end; nothing is rounded before merging. An ingredient with no such line (all
///     ToTaste, AsNeeded, optional or small frying fat) goes to CheckYouHave with no quantity (T-076).
///     Only Measured lines are scaled (ToTaste and AsNeeded never are).
///  5. Each item lists the distinct recipe ids that contributed, ascending.
/// Ordering is deterministic and independent of input order and culture: both sections sort by ingredient name
/// (OrdinalIgnoreCase, then Ordinal), then by id. The shown name is the smallest name (Ordinal) among the entries.
/// </summary>
public static class ShoppingListGenerator
{
    public static GeneratedList Generate(IEnumerable<PlannedMeal> meals, UnitCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(meals);
        ArgumentNullException.ThrowIfNull(catalog);

        var entries = meals.SelectMany(meal => meal.Ingredients.Select(i => (Meal: meal, Ingredient: i))).ToList();

        var groups = entries
            .GroupBy(e => e.Ingredient.IngredientId)
            .Select(g => (
                Id: g.Key,
                Name: g.Select(e => e.Ingredient.IngredientName).Min(StringComparer.Ordinal)!,
                IsStaple: g.Any(e => e.Ingredient.IsStaple),
                Recipes: (IReadOnlyList<Guid>)[.. g.Select(e => e.Meal.RecipeId).Distinct().Order()],
                Entries: g.ToList()))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .ThenBy(g => g.Id)
            .ToList();

        var items = new List<GeneratedItem>();
        var staples = new List<GeneratedStaple>();
        foreach (var g in groups)
        {
            if (g.IsStaple)
            {
                staples.Add(new GeneratedStaple(g.Id, g.Name, g.Recipes));
                continue;
            }

            // Sorted so that the (rare) 28-digit decimal rounding of a sum cannot depend on input order.
            var buy = g.Entries
                .Select(e => (e.Ingredient, Scaled: Scale(e.Ingredient.Kind, e.Ingredient.Quantity, e.Meal)))
                .Where(e => NeedsBuying(e.Ingredient, e.Scaled))
                .Select(e => e.Scaled)
                .OrderBy(q => q.Unit.Code, StringComparer.Ordinal)
                .ThenBy(q => q.Amount ?? -1m)
                .ToList();
            if (buy.Count == 0)
            {
                // Only "to taste", "as needed", small frying fat or optional lines: the person checks they have it.
                staples.Add(new GeneratedStaple(g.Id, g.Name, g.Recipes));
                continue;
            }

            var merged = QuantityMerger.ForDisplay(QuantityMerger.Merge(buy, catalog), catalog);
            items.Add(new GeneratedItem(g.Id, g.Name, merged, g.Recipes));
        }

        return new GeneratedList(items, staples);
    }

    /// <summary>The largest amount of frying fat that is still "check you have" (3 ss = 45 ml).</summary>
    public const decimal SmallFryingFatMilliliters = 45m;

    /// <summary>
    /// Whether a line (already scaled) is bought by amount. Not bought, only checked: ToTaste and AsNeeded lines (they have no
    /// amount), optional lines, and measured frying fat of at most <see cref="SmallFryingFatMilliliters"/> (a few
    /// spoonfuls for the pan; the household has it or buys it as a staple). Everything else is a measured line to buy.
    /// </summary>
    public static bool NeedsBuying(PlannedIngredient ingredient, Quantity scaled)
    {
        ArgumentNullException.ThrowIfNull(ingredient);
        ArgumentNullException.ThrowIfNull(scaled);
        if (ingredient.Kind != AmountKind.Measured || scaled.Amount is null || ingredient.IsOptional)
        {
            return false;
        }

        var smallFryingFat = ingredient.Purpose == IngredientPurpose.Frying
            && scaled.Unit.Dimension == UnitDimension.Volume
            && scaled.BaseAmount <= SmallFryingFatMilliliters;
        return !smallFryingFat;
    }

    /// <summary>Exact scaling: amount * planned / recipe servings. To-taste is unchanged.</summary>
    public static Quantity Scale(Quantity quantity, PlannedMeal meal) =>
        RecipeScaling.Scale(quantity, meal.RecipeServings, meal.PlannedServings);

    /// <summary>Like <see cref="Scale(Quantity, PlannedMeal)"/>, but only a Measured line scales.</summary>
    public static Quantity Scale(AmountKind kind, Quantity quantity, PlannedMeal meal) =>
        RecipeScaling.Scale(kind, quantity, meal.RecipeServings, meal.PlannedServings);
}
