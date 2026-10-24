using Foodplanner.Domain.Quantities;

namespace Foodplanner.Domain.ShoppingLists;

/// <summary>A buyable ingredient: merged quantities for display (see <see cref="QuantityMerger.ForDisplay"/>) and the recipes that contributed.</summary>
public sealed record ShoppingListItem(
    Guid IngredientId,
    string IngredientName,
    IReadOnlyList<Quantity> Quantities,
    IReadOnlyList<Guid> RecipeIds);

/// <summary>A staple the plan uses; the person checks they have it. No quantity.</summary>
public sealed record StapleItem(Guid IngredientId, string IngredientName, IReadOnlyList<Guid> RecipeIds);

public sealed record ShoppingList(IReadOnlyList<ShoppingListItem> Items, IReadOnlyList<StapleItem> CheckYouHave);

/// <summary>
/// Pure generation of a shopping list from planned meals.
///  1. Each quantity is scaled by PlannedServings / RecipeServings, exactly (decimal; multiply first, then divide).
///     To-taste stays to-taste.
///  2. Entries are grouped by ingredient id. An ingredient flagged staple in ANY entry is a staple.
///  3. Staples go only to <see cref="ShoppingList.CheckYouHave"/>, never to the main list, even with a quantity.
///  4. Other ingredients: <see cref="QuantityMerger.Merge"/> (exact), then <see cref="QuantityMerger.ForDisplay"/>.
///     Rounding happens once, at the end; nothing is rounded before merging.
///  5. Each item lists the distinct recipe ids that contributed, ascending.
/// Ordering is deterministic and independent of input order and culture: both sections sort by ingredient name
/// (OrdinalIgnoreCase, then Ordinal), then by id. The shown name is the smallest name (Ordinal) among the entries.
/// </summary>
public static class ShoppingListGenerator
{
    public static ShoppingList Generate(IEnumerable<PlannedMeal> meals, UnitCatalog catalog)
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

        var items = new List<ShoppingListItem>();
        var staples = new List<StapleItem>();
        foreach (var g in groups)
        {
            if (g.IsStaple)
            {
                staples.Add(new StapleItem(g.Id, g.Name, g.Recipes));
                continue;
            }

            // Sorted so that the (rare) 28-digit decimal rounding of a sum cannot depend on input order.
            var scaled = g.Entries
                .Select(e => Scale(e.Ingredient.Quantity, e.Meal))
                .OrderBy(q => q.Unit.Code, StringComparer.Ordinal)
                .ThenBy(q => q.Amount ?? -1m);
            var merged = QuantityMerger.ForDisplay(QuantityMerger.Merge(scaled, catalog), catalog);
            items.Add(new ShoppingListItem(g.Id, g.Name, merged, g.Recipes));
        }

        return new ShoppingList(items, staples);
    }

    /// <summary>Exact scaling: amount * planned / recipe servings. To-taste is unchanged.</summary>
    public static Quantity Scale(Quantity quantity, PlannedMeal meal) =>
        quantity.Amount is { } amount
            ? new Quantity(amount * meal.PlannedServings / meal.RecipeServings, quantity.Unit)
            : quantity;
}
